#ifndef NJULF_DDGI_SIMPLE_SHARED_GLSL
#define NJULF_DDGI_SIMPLE_SHARED_GLSL

#include "farfield_clipmap.glsl"

// Only the forward fragment path has a meaningful screen-tile coordinate.
// Compute, fog, and particle stages retain the canonical bounded table walk.
// The optional sampled-image mirror is graphics-queue owned. Compute transport
// must read the canonical SSBO field so it never consumes a stale mirror or
// requires an image queue-ownership transfer in the DDGI transaction.
#ifndef SIMPLE_DDGI_DISABLE_SAMPLED_ATLAS
#define SIMPLE_DDGI_DISABLE_SAMPLED_ATLAS 0
#endif

// Release/ShippingPerformance pass zero from the shader project. This is a
// compile-time boundary, not a runtime feature flag: production SPIR-V contains
// no reachable DDGI investigation atomic or diagnostic sampling branch.
#ifndef NJULF_DDGI_DETAILED_COUNTERS
#define NJULF_DDGI_DETAILED_COUNTERS 0
#endif

const float SIMPLE_DDGI_PI = 3.14159265359;
const uint SIMPLE_DDGI_FLAG_ENABLED = 1u << 0;
const uint SIMPLE_DDGI_FLAG_FAR_FIELD_ENABLED = 1u << 1;
const uint SIMPLE_DDGI_FLAG_FAR_FIELD_FORCE_ALL = 1u << 2;
const uint SIMPLE_DDGI_FLAG_FOG_ENABLED = 1u << 3;
const uint SIMPLE_DDGI_FLAG_PARTICLE_ENABLED = 1u << 4;
const uint SIMPLE_DDGI_FLAG_ADAPTIVE_HYSTERESIS = 1u << 5;
const uint SIMPLE_DDGI_FLAG_SKY_VISIBILITY_ENABLED = 1u << 6;
const uint SIMPLE_DDGI_FLAG_FAR_SUN_SHADOW_ENABLED = 1u << 7;
// Feature gate for the support-aware gather/composition path.  When disabled,
// callers receive explicit no-support and can take the environment-only safety
// fallback rather than sampling an obsolete atlas convention.
const uint SIMPLE_DDGI_FLAG_STRUCTURED_GATHER_ENABLED = 1u << 8;
// Detailed investigation counters are disabled in normal production rendering
// so per-ray/per-texel atomic traffic is never part of the steady-state cost.
const uint SIMPLE_DDGI_FLAG_DETAILED_DIAGNOSTICS_ENABLED = 1u << 9;
// The CPU knows when a real lighting edit is propagating.  Adaptive history must
// not mistake ordinary low-sample Monte-Carlo variation for that edit.
const uint SIMPLE_DDGI_FLAG_LIGHTING_CHANGE_ACTIVE = 1u << 10;
// V2 separates traced source transport from cached recursive solve. The
// canonical published irradiance atlas remains the sole receiver-visible field.
const uint SIMPLE_DDGI_FLAG_TRANSPORT_V2 = 1u << 11;
// Bits 12..19 contain the packed second-volume ownership threshold.
const uint SIMPLE_DDGI_FLAG_THIN_SURFACE_TRANSMISSION = 1u << 20;
const uint SIMPLE_DDGI_FLAG_FORCE_LEGACY_FAR_FIELD_FALLBACK = 1u << 21;
// A normalized [0, 1] second-volume early-out threshold is packed into the
// otherwise-unused high flag bits. This preserves the fixed params header ABI.
const uint SIMPLE_DDGI_SECOND_VOLUME_OWNERSHIP_THRESHOLD_SHIFT = 12u;
const uint SIMPLE_DDGI_SECOND_VOLUME_OWNERSHIP_THRESHOLD_MASK = 0xffu << SIMPLE_DDGI_SECOND_VOLUME_OWNERSHIP_THRESHOLD_SHIFT;
const uint SIMPLE_DDGI_IRRADIANCE_TEXELS = 8u;
const uint SIMPLE_DDGI_VISIBILITY_TEXELS = 16u;
const uint SIMPLE_DDGI_MAX_RAYS_PER_PROBE = 256u;
const uint SIMPLE_DDGI_RAY_RESULT_STRIDE_WORDS = 8u;
const uint SIMPLE_DDGI_TRANSPORT_RAY_CACHE_ABI_VERSION = 2u;
const uint SIMPLE_DDGI_HEADER_WORDS = 52u;
const uint SIMPLE_DDGI_FLAGS_WORD = 14u;
const uint SIMPLE_DDGI_VOLUME_STRIDE_WORDS = 24u;
const uint SIMPLE_DDGI_MAX_VOLUME_COUNT = 16u;
// The hot path normally samples one authoritative volume and one fallback.
// Additional coarser recovery volumes are sampled only while the accumulated
// ownership remains below the minimum support ramp. This closes inactive-probe
// holes without turning healthy overlaps into extra eight-corner gathers.
// Two recovery gathers cover the production hierarchy with the greatest depth:
// one authored volume plus the near/mid/far clipmap rings. The environment
// complement remains the defined boundary condition if all four sampled fields
// are unsupported. Capping full eight-corner gathers independently from the
// complete metadata walk prevents a pathological stack of authored volumes
// from expanding one fragment or solver ray into sixteen atlas gathers.
const uint SIMPLE_DDGI_MAX_RECOVERY_GATHER_SAMPLES = 2u;
// The CPU clamps the uploaded table to SIMPLE_DDGI_MAX_VOLUME_COUNT. Keep the
// selection loops statically bounded as well: drivers can unroll the small
// metadata-only walk, while a corrupt runtime count cannot turn one shaded
// receiver into an unbounded SSBO search.
const uint SIMPLE_DDGI_MAX_SELECTION_VOLUME_CHECKS = SIMPLE_DDGI_MAX_VOLUME_COUNT;
// A late, valid fallback must still be discoverable (for example after many
// non-overlapping authored volumes), so each bounded metadata walk can inspect
// the complete remaining table rather than using an arbitrary coverage cap.
const uint SIMPLE_DDGI_MAX_GATHER_FALLBACK_CANDIDATE_CHECKS =
    SIMPLE_DDGI_MAX_VOLUME_COUNT - 1u;
const uint SIMPLE_DDGI_GATHER_ROLE_PRIMARY = 0u;
const uint SIMPLE_DDGI_GATHER_ROLE_FALLBACK = 1u;
const uint SIMPLE_DDGI_GATHER_ROLE_RECOVERY = 2u;
const uint SIMPLE_DDGI_GATHER_ROLE_COUNT = 3u;
const uint SIMPLE_DDGI_GATHER_REJECTION_REASON_COUNT = 9u;
const uint SIMPLE_DDGI_GATHER_REJECT_FRESH = 1u << 0;
const uint SIMPLE_DDGI_GATHER_REJECT_SCROLL_EXPOSED = 1u << 1;
const uint SIMPLE_DDGI_GATHER_REJECT_RELOCATION_PENDING = 1u << 2;
const uint SIMPLE_DDGI_GATHER_REJECT_INACTIVE_FLAG = 1u << 3;
const uint SIMPLE_DDGI_GATHER_REJECT_INACTIVE_CLASSIFICATION = 1u << 4;
const uint SIMPLE_DDGI_GATHER_REJECT_ZERO_ACTIVE_WEIGHT = 1u << 5;
const uint SIMPLE_DDGI_GATHER_REJECT_INVALID_IRRADIANCE = 1u << 6;
const uint SIMPLE_DDGI_GATHER_REJECT_INVALID_VISIBILITY = 1u << 7;
const uint SIMPLE_DDGI_GATHER_REJECT_OUTSIDE_DOMAIN = 1u << 8;
// Appended after the MaterialGi diagnostic family. Detailed diagnostics use a
// sparse forward sample, so production gather frames pay no atomic cost.
const uint SIMPLE_DDGI_GATHER_REJECTION_COUNTER_BASE = 262u;
const uint SIMPLE_DDGI_GATHER_ALL_FAILED_COUNTER_BASE =
    SIMPLE_DDGI_GATHER_REJECTION_COUNTER_BASE +
    SIMPLE_DDGI_GATHER_ROLE_COUNT * SIMPLE_DDGI_GATHER_REJECTION_REASON_COUNT;
// The fixed-size Simple-DDGI candidate table uses its dedicated scheduler arena
// binding. An unavailable or untagged table takes the bounded full-table
// selector. A projected-candidate overflow remains a heatmap signal, not a
// reason to discard the two retained highest-priority candidates.
const uint SIMPLE_DDGI_INVALID_VOLUME_INDEX = 0xffffffffu;
const uint SIMPLE_DDGI_VOLUME_KIND_LEGACY = 0u;
const uint SIMPLE_DDGI_VOLUME_KIND_AUTHORED = 1u;
const uint SIMPLE_DDGI_VOLUME_KIND_RING = 2u;
// A small support ramp prevents a single barely-valid trilinear corner from
// abruptly taking ownership of the entire receiver when a fresh probe settles.
const float SIMPLE_DDGI_OWNERSHIP_SUPPORT_RAMP = 0.15;
// Directional support is geometric estimator authority, not probe-data
// availability or transport visibility. Keep its ownership ramp distinct so
// an all-back-facing cell can yield to a coarser gather/environment without
// relabelling healthy probe data as unavailable.
const float SIMPLE_DDGI_OWNERSHIP_DIRECTIONAL_SUPPORT_RAMP = 0.15;
// The low/high interval is reserved for the late all-occluded leak safeguard.
// Normalized probe selection uses the cubic Chebyshev confidence below.
const float SIMPLE_DDGI_VISIBILITY_SELECTION_LOW = 0.01;
const float SIMPLE_DDGI_VISIBILITY_SELECTION_HIGH = 0.08;
// State-valid probes retain a small interpolation share even when conservative
// visibility moments are temporarily over-occluded. Transport visibility still
// suppresses their radiance, while this floor prevents every corner in a dense
// architectural cell from disappearing at once.
const float SIMPLE_DDGI_VISIBILITY_SELECTION_FLOOR = 0.05;
// Variance uncertainty grows through the fine and first coarse-probe ranges.
// The mean-distance bound below remains authoritative at thin occluders, while
// this cap prevents valid coarse-ring support from collapsing numerically.
const float SIMPLE_DDGI_VISIBILITY_VARIANCE_SPACING_CAP = 4.0;
// Recursive transport consumes a normalized estimator, so genuine probe support
// should rapidly acquire solver authority. Receiver composition deliberately
// keeps the broader ownership ramp and stronger leak suppression below.
const float SIMPLE_DDGI_SOLVER_OWNERSHIP_SUPPORT_RAMP = 0.02;
// Missing probe ownership may attenuate sky bounce, but must never annihilate it.
const float SIMPLE_DDGI_MINIMUM_SKY_VISIBILITY = 0.10;
// Keep every environment-fallback call site on one numerical contract. Below
// this contribution the FP16 lighting pipeline cannot retain a meaningful
// delta, while entering the far-field cone tracer is exceptionally expensive.
const float SIMPLE_DDGI_ENVIRONMENT_FALLBACK_MIN_WEIGHT = 0.0001;
const uint SIMPLE_DDGI_AUTHORED_VOLUME_CASCADE = 0xffffffffu;
const uint SIMPLE_DDGI_PROBE_STATE_STRIDE_WORDS = 8u;
const uint SIMPLE_DDGI_PROBE_UPDATE_STRIDE_WORDS = 8u;
const uint SIMPLE_DDGI_RELOCATION_CLASSIFICATION_STRIDE_WORDS = 12u;
const uint SIMPLE_DDGI_PROBE_FLAG_FRESH = 1u << 0;
const uint SIMPLE_DDGI_PROBE_FLAG_SCROLL_EXPOSED = 1u << 1;
const uint SIMPLE_DDGI_PROBE_FLAG_INACTIVE = 1u << 2;
// Relocation is committed after tracing, so the rays produced by that update
// belong to the previous probe position. Keep the probe unavailable until a
// later full update traces from the committed position and republishes both
// atlases as one spatially coherent transaction.
const uint SIMPLE_DDGI_PROBE_FLAG_RELOCATION_PENDING = 1u << 3;
// A cache-validity failure is recoverable, but it must not be allowed to turn
// into a long-lived partial source field. Trace/transport atomically mark this
// bit and the CPU readback invalidates the physical slot for one full source
// refresh on the following transaction.
const uint SIMPLE_DDGI_PROBE_FLAG_SOURCE_CACHE_INVALID = 1u << 4;
const uint SIMPLE_DDGI_UPDATE_MATERIAL_TEXTURE_CASCADE_SHIFT = 3u;
const uint SIMPLE_DDGI_UPDATE_MATERIAL_TEXTURE_CASCADE_MASK = 0x7u << SIMPLE_DDGI_UPDATE_MATERIAL_TEXTURE_CASCADE_SHIFT;
const uint SIMPLE_DDGI_UPDATE_MAX_SHADED_LIGHTS_SHIFT = 6u;
const uint SIMPLE_DDGI_UPDATE_MAX_SHADED_LIGHTS_MASK = 0x3fu << SIMPLE_DDGI_UPDATE_MAX_SHADED_LIGHTS_SHIFT;
const uint SIMPLE_DDGI_UPDATE_MAINTENANCE = 1u << 12;
const uint SIMPLE_DDGI_UPDATE_SOURCE_REFRESH = 1u << 13;
// The remaining state-flag bits carry a non-zero physical-slot generation.  An
// update recorded for an old toroidal mapping must never mutate the slot after it
// has been re-exposed for a new world cell.
const uint SIMPLE_DDGI_PROBE_FLAG_GENERATION_SHIFT = 8u;
const uint SIMPLE_DDGI_PROBE_FLAG_GENERATION_MASK = 0xffffff00u;
const uint SIMPLE_DDGI_SCHEDULER_INVALID_INDEX = 0xffffffffu;
const uint SIMPLE_DDGI_SCHEDULER_RAY_BUCKET_STRIDE_WORDS = 4u;
const uint SIMPLE_DDGI_SCHEDULER_COUNTER_ACCEPTED_WORD = 2u;

bool SimpleDdgiGpuSchedulerActive(uint arenaIndex)
{
    return arenaIndex != SIMPLE_DDGI_SCHEDULER_INVALID_INDEX;
}

uint SimpleDdgiSchedulerRead(uint arenaIndex, uint wordOffset)
{
    return ReadStorageWordUniform(arenaIndex, wordOffset);
}

uint SimpleDdgiSchedulerAcceptedCount(uint arenaIndex, uint countersOffsetWords)
{
    return SimpleDdgiSchedulerRead(
        arenaIndex,
        countersOffsetWords + SIMPLE_DDGI_SCHEDULER_COUNTER_ACCEPTED_WORD);
}

void SimpleDdgiResolveSchedulerRayBucket(
    uint arenaIndex,
    uint bucketCommandsOffsetWords,
    uint bucketIndex,
    out uint queueOffset,
    out uint probeCount,
    out uint raysPerProbe)
{
    uint base = bucketCommandsOffsetWords +
        bucketIndex * SIMPLE_DDGI_SCHEDULER_RAY_BUCKET_STRIDE_WORDS;
    queueOffset = SimpleDdgiSchedulerRead(arenaIndex, base + 1u);
    probeCount = SimpleDdgiSchedulerRead(arenaIndex, base + 2u);
    raysPerProbe = max(1u, SimpleDdgiSchedulerRead(arenaIndex, base + 3u));
}

// A one-sided shell seen from behind is neither a shadeable surface nor sky.
// Keep it as a distinct ray outcome so visibility/relocation retain the wall
// distance while irradiance estimators can leave the direction unrepresented.
const float SIMPLE_DDGI_RAY_HIT_KIND_MISS = 0.0;
const float SIMPLE_DDGI_RAY_HIT_KIND_FRONT_FACE = 1.0;
const float SIMPLE_DDGI_RAY_HIT_KIND_BACK_FACE = 2.0;
const float SIMPLE_DDGI_RAY_HIT_KIND_ONE_SIDED_BACK_FACE = 3.0;
// Far-field voxels are closed occupancy evidence rather than authored
// double-sided sheets, so preserve that provenance for relocation.
const float SIMPLE_DDGI_RAY_HIT_KIND_FAR_FIELD_BACK_FACE = 4.0;

uint PackSimpleDdgiRayVisibilityHit(float visibilityDistance, float hitKind)
{
    return packHalf2x16(vec2(
        clamp(visibilityDistance, 0.0, 65504.0),
        clamp(hitKind, 0.0, SIMPLE_DDGI_RAY_HIT_KIND_FAR_FIELD_BACK_FACE)));
}

bool SimpleDdgiRayHitKindIsOneSidedBackFace(float hitKind)
{
    // Coarse far-field occupancy is a closed solid and already has a shaded
    // radiance result. Its reconstructed normal may face either way, so only
    // the explicit authored one-sided reverse-face outcome is non-publishable.
    return abs(hitKind - SIMPLE_DDGI_RAY_HIT_KIND_ONE_SIDED_BACK_FACE) < 0.25;
}

vec2 UnpackSimpleDdgiRayVisibilityHit(uint packedVisibilityHit)
{
    return unpackHalf2x16(packedVisibilityHit);
}

const uint SIMPLE_DDGI_UPDATE_RAY_COUNT_SHIFT = 16u;
const uint SIMPLE_DDGI_UPDATE_RAY_COUNT_MASK = 0xffff0000u;
const uint SIMPLE_DDGI_UPDATE_GENERATION_MASK = 0x00ffffffu;
const uint SIMPLE_DDGI_UPDATE_AGE_SHIFT = 24u;
const uint SIMPLE_DDGI_UPDATE_AGE_MASK = 0xff000000u;
const uint SIMPLE_DDGI_RELOCATION_PENDING_MAX_RETRY_AGE = 32u;
const uint SIMPLE_DDGI_CLASSIFICATION_ACTIVE = 0u;
const uint SIMPLE_DDGI_CLASSIFICATION_INACTIVE = 1u;

struct SimpleDdgiParams
{
    vec3 origin;
    float spacing;
    uvec3 gridCount;
    uint probeCount;
    uint irradianceTexels;
    uint visibilityTexels;
    uint raysPerProbe;
    uint farFieldResolution;
    float hysteresis;
    uint frameIndex;
    uint flags;
    float farFieldStartDistance;
    vec3 environmentRadiance;
    float environmentIntensity;
    float environmentFallbackIntensity;
    uint probesToUpdate;
    float selfShadowBiasScale;
    float indirectIntensity;
    uint debugView;
    uint farFieldMaxTraceSteps;
    vec4 rayRotation;
    float normalBias;
    float viewBias;
    float hysteresisChangeThreshold;
    float hysteresisStepThreshold;
    uint volumeCount;
    // Optional sampled-image mirror metadata. The SSBO remains canonical and
    // handles octahedral seam filtering, while images accelerate interior taps.
    uint sampledAtlasLayersPerTexture;
    uint sampledAtlasTextureGroupCount;
    uint sampledAtlasEnabled;
    float secondVolumeOwnershipEarlyOutThreshold;
    float maximumWorldBias;
    float architecturalThickness;
    float thinWallLeakClampStrength;
    uint publishedIrradianceAtlasBufferIndex;
    uint transportTargetIrradianceAtlasBufferIndex;
    uint transportSourceCacheBufferIndex;
    uint transportGeneration;
    float transportSolverRelaxation;
    float transportAlbedoClamp;
    float transportResidualThreshold;
    uint transportMaximumSolverGenerations;
};

bool SimpleDdgiDetailedDiagnosticsEnabled(SimpleDdgiParams params)
{
#if NJULF_DDGI_DETAILED_COUNTERS
    return (params.flags & SIMPLE_DDGI_FLAG_DETAILED_DIAGNOSTICS_ENABLED) != 0u;
#else
    return false;
#endif
}

void AddSimpleDdgiDiagnostic(SimpleDdgiParams params, uint frameIndex, uint counterIndex, uint value)
{
#if NJULF_DDGI_DETAILED_COUNTERS
    if (SimpleDdgiDetailedDiagnosticsEnabled(params))
        AddRendererDiagnostic(frameIndex, counterIndex, value);
#endif
}

// These renderer-owned slots describe the active DDGI transport path. Full and
// simple DDGI are mutually exclusive, so the simple implementation can publish
// the same capture contract without allocating a second diagnostics ABI.
const uint SIMPLE_DDGI_TRACE_ENERGY_COUNTER_BASE = 55u;
const uint SIMPLE_DDGI_TRACE_ENERGY_SAMPLE_COUNT_COUNTER = SIMPLE_DDGI_TRACE_ENERGY_COUNTER_BASE + 0u;
const uint SIMPLE_DDGI_TRACE_ENERGY_HIT_COUNT_COUNTER = SIMPLE_DDGI_TRACE_ENERGY_COUNTER_BASE + 1u;
const uint SIMPLE_DDGI_TRACE_ENERGY_MISS_COUNT_COUNTER = SIMPLE_DDGI_TRACE_ENERGY_COUNTER_BASE + 2u;
const uint SIMPLE_DDGI_TRACE_ENERGY_RAY_LUMINANCE_COUNTER = SIMPLE_DDGI_TRACE_ENERGY_COUNTER_BASE + 3u;
const uint SIMPLE_DDGI_TRACE_ENERGY_DIRECT_LUMINANCE_COUNTER = SIMPLE_DDGI_TRACE_ENERGY_COUNTER_BASE + 4u;
const uint SIMPLE_DDGI_TRACE_ENERGY_EMISSIVE_LUMINANCE_COUNTER = SIMPLE_DDGI_TRACE_ENERGY_COUNTER_BASE + 5u;
const uint SIMPLE_DDGI_TRACE_ENERGY_BOUNCE_LUMINANCE_COUNTER = SIMPLE_DDGI_TRACE_ENERGY_COUNTER_BASE + 6u;
const uint SIMPLE_DDGI_TRACE_ENERGY_SKY_LUMINANCE_COUNTER = SIMPLE_DDGI_TRACE_ENERGY_COUNTER_BASE + 7u;
const uint SIMPLE_DDGI_TRACE_ENERGY_HIT_ZERO_DIRECT_COUNTER = SIMPLE_DDGI_TRACE_ENERGY_COUNTER_BASE + 8u;
const uint SIMPLE_DDGI_TRACE_ENERGY_HIT_WITH_DIRECT_COUNTER = SIMPLE_DDGI_TRACE_ENERGY_COUNTER_BASE + 9u;
const uint SIMPLE_DDGI_TRACE_ENERGY_DIRECT_NO_SHADOW_LUMINANCE_COUNTER = SIMPLE_DDGI_TRACE_ENERGY_COUNTER_BASE + 10u;
const uint SIMPLE_DDGI_BLEND_ENERGY_COUNTER_BASE = 72u;
const uint SIMPLE_DDGI_BLEND_ENERGY_SAMPLE_COUNT_COUNTER = SIMPLE_DDGI_BLEND_ENERGY_COUNTER_BASE + 0u;
const uint SIMPLE_DDGI_BLEND_ENERGY_IRRADIANCE_LUMINANCE_COUNTER = SIMPLE_DDGI_BLEND_ENERGY_COUNTER_BASE + 1u;
const uint SIMPLE_DDGI_BLEND_ENERGY_CONFIDENCE_COUNTER = SIMPLE_DDGI_BLEND_ENERGY_COUNTER_BASE + 2u;
const uint SIMPLE_DDGI_BLEND_ENERGY_LOW_CONFIDENCE_COUNTER = SIMPLE_DDGI_BLEND_ENERGY_COUNTER_BASE + 3u;
const uint SIMPLE_DDGI_BLEND_ENERGY_NONZERO_IRRADIANCE_COUNTER = SIMPLE_DDGI_BLEND_ENERGY_COUNTER_BASE + 4u;
// Appended renderer diagnostics ABI. One bank per declared Simple-DDGI volume:
// blend count/luminance/confidence, transport count/source/bounce/total,
// solver gather count/ownership/fallback, and one-sided reverse-face count.
const uint SIMPLE_DDGI_VOLUME_ENERGY_COUNTER_BASE = 321u;
const uint SIMPLE_DDGI_VOLUME_ENERGY_COUNTER_STRIDE = 19u;
const uint SIMPLE_DDGI_VOLUME_ENERGY_COUNTER_COUNT = 16u;
const float SIMPLE_DDGI_ENERGY_LUMINANCE_SCALE = 4096.0;
const float SIMPLE_DDGI_ENERGY_WEIGHT_SCALE = 1024.0;

float SimpleDdgiEnergyLuminance(vec3 value)
{
    return dot(max(value, vec3(0.0)), vec3(0.2126, 0.7152, 0.0722));
}

uint PackSimpleDdgiEnergyLuminance(float value)
{
    return uint(round(clamp(value, 0.0, 16.0) * SIMPLE_DDGI_ENERGY_LUMINANCE_SCALE));
}

uint PackSimpleDdgiEnergyWeight(float value)
{
    return uint(round(clamp(value, 0.0, 1.0) * SIMPLE_DDGI_ENERGY_WEIGHT_SCALE));
}

uint PackSimpleDdgiEnergyWeightSum(float value)
{
    return uint(round(clamp(value, 0.0, 16.0) * SIMPLE_DDGI_ENERGY_WEIGHT_SCALE));
}

bool SimpleDdgiTraceEnergyDiagnosticRay(SimpleDdgiParams params, uint probeIndex, uint rayIndex)
{
    return SimpleDdgiDetailedDiagnosticsEnabled(params) &&
        ((probeIndex + rayIndex + params.frameIndex) & 3u) == 0u;
}

bool SimpleDdgiBlendEnergyDiagnosticTexel(SimpleDdgiParams params, uint probeIndex, uint texel)
{
    return SimpleDdgiDetailedDiagnosticsEnabled(params) &&
        ((probeIndex + texel + params.frameIndex) & 7u) == 0u;
}

void RecordSimpleDdgiTraceEnergyDiagnostics(
    SimpleDdgiParams params,
    uint diagnosticFrame,
    uint probeIndex,
    uint rayIndex,
    vec3 rayRadiance,
    vec3 directDiffuse,
    vec3 directNoShadowDiffuse,
    vec3 emissiveDiffuse,
    vec3 bounceDiffuse,
    vec3 skyDiffuse,
    bool hit)
{
    if (!SimpleDdgiTraceEnergyDiagnosticRay(params, probeIndex, rayIndex))
        return;

    AddRendererDiagnostic(diagnosticFrame, SIMPLE_DDGI_TRACE_ENERGY_SAMPLE_COUNT_COUNTER, 1u);
    AddRendererDiagnostic(diagnosticFrame, SIMPLE_DDGI_TRACE_ENERGY_RAY_LUMINANCE_COUNTER, PackSimpleDdgiEnergyLuminance(SimpleDdgiEnergyLuminance(rayRadiance)));
    AddRendererDiagnostic(diagnosticFrame, SIMPLE_DDGI_TRACE_ENERGY_DIRECT_LUMINANCE_COUNTER, PackSimpleDdgiEnergyLuminance(SimpleDdgiEnergyLuminance(directDiffuse)));
    AddRendererDiagnostic(diagnosticFrame, SIMPLE_DDGI_TRACE_ENERGY_DIRECT_NO_SHADOW_LUMINANCE_COUNTER, PackSimpleDdgiEnergyLuminance(SimpleDdgiEnergyLuminance(directNoShadowDiffuse)));
    AddRendererDiagnostic(diagnosticFrame, SIMPLE_DDGI_TRACE_ENERGY_EMISSIVE_LUMINANCE_COUNTER, PackSimpleDdgiEnergyLuminance(SimpleDdgiEnergyLuminance(emissiveDiffuse)));
    AddRendererDiagnostic(diagnosticFrame, SIMPLE_DDGI_TRACE_ENERGY_BOUNCE_LUMINANCE_COUNTER, PackSimpleDdgiEnergyLuminance(SimpleDdgiEnergyLuminance(bounceDiffuse)));
    AddRendererDiagnostic(diagnosticFrame, SIMPLE_DDGI_TRACE_ENERGY_SKY_LUMINANCE_COUNTER, PackSimpleDdgiEnergyLuminance(SimpleDdgiEnergyLuminance(skyDiffuse)));

    if (hit)
    {
        AddRendererDiagnostic(diagnosticFrame, SIMPLE_DDGI_TRACE_ENERGY_HIT_COUNT_COUNTER, 1u);
        AddRendererDiagnostic(
            diagnosticFrame,
            SimpleDdgiEnergyLuminance(directDiffuse) <= 0.00001
                ? SIMPLE_DDGI_TRACE_ENERGY_HIT_ZERO_DIRECT_COUNTER
                : SIMPLE_DDGI_TRACE_ENERGY_HIT_WITH_DIRECT_COUNTER,
            1u);
    }
    else
    {
        AddRendererDiagnostic(diagnosticFrame, SIMPLE_DDGI_TRACE_ENERGY_MISS_COUNT_COUNTER, 1u);
    }
}

void RecordSimpleDdgiBlendEnergyDiagnostics(
    SimpleDdgiParams params,
    uint diagnosticFrame,
    uint volumeIndex,
    uint probeIndex,
    uint texel,
    vec3 irradiance,
    float confidence)
{
    if (!SimpleDdgiBlendEnergyDiagnosticTexel(params, probeIndex, texel))
        return;

    float luminance = SimpleDdgiEnergyLuminance(irradiance);
    float safeConfidence = clamp(confidence, 0.0, 1.0);
    AddRendererDiagnostic(diagnosticFrame, SIMPLE_DDGI_BLEND_ENERGY_SAMPLE_COUNT_COUNTER, 1u);
    AddRendererDiagnostic(diagnosticFrame, SIMPLE_DDGI_BLEND_ENERGY_IRRADIANCE_LUMINANCE_COUNTER, PackSimpleDdgiEnergyLuminance(luminance));
    AddRendererDiagnostic(diagnosticFrame, SIMPLE_DDGI_BLEND_ENERGY_CONFIDENCE_COUNTER, PackSimpleDdgiEnergyWeight(safeConfidence));
    if (safeConfidence <= 0.0001)
        AddRendererDiagnostic(diagnosticFrame, SIMPLE_DDGI_BLEND_ENERGY_LOW_CONFIDENCE_COUNTER, 1u);
    if (luminance > 0.00001)
        AddRendererDiagnostic(diagnosticFrame, SIMPLE_DDGI_BLEND_ENERGY_NONZERO_IRRADIANCE_COUNTER, 1u);

    if (volumeIndex < SIMPLE_DDGI_VOLUME_ENERGY_COUNTER_COUNT)
    {
        uint bank = SIMPLE_DDGI_VOLUME_ENERGY_COUNTER_BASE +
            volumeIndex * SIMPLE_DDGI_VOLUME_ENERGY_COUNTER_STRIDE;
        AddRendererDiagnostic(diagnosticFrame, bank + 0u, 1u);
        AddRendererDiagnostic(diagnosticFrame, bank + 1u, PackSimpleDdgiEnergyLuminance(luminance));
        AddRendererDiagnostic(diagnosticFrame, bank + 2u, PackSimpleDdgiEnergyWeight(safeConfidence));
    }
}

// V2 traces source radiance and solves the reflected component in a separate
// pass. Keep those energy terms distinct in captures: otherwise a correct
// cache-reuse solve looks like a source-only trace with zero bounce.
void RecordSimpleDdgiTransportEnergyDiagnostics(
    SimpleDdgiParams params,
    uint diagnosticFrame,
    uint volumeIndex,
    uint probeIndex,
    uint directionRayIndex,
    bool sourceCacheHit,
    vec3 sourceRadiance,
    vec3 bounceRadiance,
    vec3 totalRadiance,
    uint solverGatherCount,
    float solverOwnershipSum,
    float solverFallbackWeightSum)
{
    if (!SimpleDdgiTraceEnergyDiagnosticRay(params, probeIndex, directionRayIndex))
        return;

    AddRendererDiagnostic(diagnosticFrame, SIMPLE_DDGI_TRANSPORT_SAMPLE_COUNT_COUNTER, 1u);
    AddRendererDiagnostic(
        diagnosticFrame,
        sourceCacheHit
            ? SIMPLE_DDGI_TRANSPORT_SOURCE_CACHE_HIT_COUNTER
            : SIMPLE_DDGI_TRANSPORT_SOURCE_CACHE_MISS_COUNTER,
        1u);
    AddRendererDiagnostic(
        diagnosticFrame,
        SIMPLE_DDGI_TRANSPORT_BOUNCE_LUMINANCE_COUNTER,
        PackSimpleDdgiEnergyLuminance(SimpleDdgiEnergyLuminance(bounceRadiance)));
    AddRendererDiagnostic(
        diagnosticFrame,
        SIMPLE_DDGI_TRANSPORT_SOURCE_LUMINANCE_COUNTER,
        PackSimpleDdgiEnergyLuminance(SimpleDdgiEnergyLuminance(sourceRadiance)));
    AddRendererDiagnostic(
        diagnosticFrame,
        SIMPLE_DDGI_TRANSPORT_TOTAL_LUMINANCE_COUNTER,
        PackSimpleDdgiEnergyLuminance(SimpleDdgiEnergyLuminance(totalRadiance)));

    if (volumeIndex < SIMPLE_DDGI_VOLUME_ENERGY_COUNTER_COUNT)
    {
        uint bank = SIMPLE_DDGI_VOLUME_ENERGY_COUNTER_BASE +
            volumeIndex * SIMPLE_DDGI_VOLUME_ENERGY_COUNTER_STRIDE;
        AddRendererDiagnostic(diagnosticFrame, bank + 3u, 1u);
        AddRendererDiagnostic(diagnosticFrame, bank + 4u,
            PackSimpleDdgiEnergyLuminance(SimpleDdgiEnergyLuminance(sourceRadiance)));
        AddRendererDiagnostic(diagnosticFrame, bank + 5u,
            PackSimpleDdgiEnergyLuminance(SimpleDdgiEnergyLuminance(bounceRadiance)));
        AddRendererDiagnostic(diagnosticFrame, bank + 6u,
            PackSimpleDdgiEnergyLuminance(SimpleDdgiEnergyLuminance(totalRadiance)));
        AddRendererDiagnostic(diagnosticFrame, bank + 7u, solverGatherCount);
        AddRendererDiagnostic(diagnosticFrame, bank + 8u,
            PackSimpleDdgiEnergyWeightSum(solverOwnershipSum));
        AddRendererDiagnostic(diagnosticFrame, bank + 9u,
            PackSimpleDdgiEnergyWeightSum(solverFallbackWeightSum));
    }
}

void RecordSimpleDdgiOneSidedBackFaceDiagnostic(
    SimpleDdgiParams params,
    uint diagnosticFrame,
    uint volumeIndex)
{
    if (!SimpleDdgiDetailedDiagnosticsEnabled(params) ||
        volumeIndex >= SIMPLE_DDGI_VOLUME_ENERGY_COUNTER_COUNT)
    {
        return;
    }

    uint bank = SIMPLE_DDGI_VOLUME_ENERGY_COUNTER_BASE +
        volumeIndex * SIMPLE_DDGI_VOLUME_ENERGY_COUNTER_STRIDE;
    AddRendererDiagnostic(diagnosticFrame, bank + 10u, 1u);
}

#ifndef SIMPLE_DDGI_GATHER_DIAGNOSTIC_SAMPLE_WEIGHT
#define SIMPLE_DDGI_GATHER_DIAGNOSTIC_SAMPLE_WEIGHT 1u
#endif

// Forward shading overrides the weight with a sparse screen-tile sample.  The
// compute users of this shared include retain exact one-per-event counters.
void AddSimpleDdgiGatherDiagnostic(SimpleDdgiParams params, uint frameIndex, uint counterIndex)
{
    if (!SimpleDdgiDetailedDiagnosticsEnabled(params))
        return;

    uint sampleWeight = SIMPLE_DDGI_GATHER_DIAGNOSTIC_SAMPLE_WEIGHT;
    if (sampleWeight != 0u)
        AddRendererDiagnostic(frameIndex, counterIndex, sampleWeight);
}

struct SimpleDdgiVolume
{
    vec3 origin;
    float spacing;
    uvec3 gridCount;
    uint firstProbeIndex;
    vec3 worldMin;
    float edgeFadeDistance;
    vec3 worldMax;
    uint kind;
    uint probesToUpdate;
    uint sourceOrdinal;
    uvec3 physicalOffset;
};

struct SimpleDdgiDebugSample
{
    uint probeIndex;
    uint volumeIndex;
    vec3 logicalProbePosition;
    vec3 relocatedProbePosition;
    float visibility;
    float visibilityConfidence;
    float visibilityMomentMean;
    float visibilityMomentVariance;
    float visibilityProbeDistance;
    float visibilityMaxRayDistance;
};

struct SimpleDdgiProbeState
{
    vec3 relocation;
    float activeWeight;
    uint flags;
    uint age;
    uint classification;
    float luminanceChangeEma;
};

struct SimpleDdgiProbeUpdate
{
    uint probeIndex;
    uint volumeIndex;
    uint flags;
    uint expectedGeneration;
    // The full source sequence cardinality for this physical probe. The queue's
    // active ray count can be a smaller maintenance subset, but cache lookup must
    // select within this original sequence to reuse the exact traced directions.
    uint sourceRayCount;
};

uint SimpleDdgiProbeStateBase(uint probeIndex)
{
    return probeIndex * SIMPLE_DDGI_PROBE_STATE_STRIDE_WORDS;
}

SimpleDdgiProbeState ReadSimpleDdgiProbeState(uint bufferIndex, uint probeIndex)
{
    uint baseWord = SimpleDdgiProbeStateBase(probeIndex);
    vec4 relocationAndActive = ReadStorageAlignedVec4Uniform(bufferIndex, baseWord);
    uvec4 metadata = ReadStorageAlignedUVec4Uniform(bufferIndex, baseWord + 4u);
    SimpleDdgiProbeState state;
    state.relocation = relocationAndActive.xyz;
    state.activeWeight = relocationAndActive.w;
    state.flags = metadata.x;
    state.age = metadata.y;
    state.classification = metadata.z;
    state.luminanceChangeEma = uintBitsToFloat(metadata.w);
    return state;
}

void WriteSimpleDdgiProbeState(uint bufferIndex, uint probeIndex, SimpleDdgiProbeState state)
{
    uint baseWord = SimpleDdgiProbeStateBase(probeIndex);
    WriteStorageVec4Uniform(bufferIndex, baseWord, vec4(state.relocation, state.activeWeight));
    WriteStorageWordUniform(bufferIndex, baseWord + 4u, state.flags);
    WriteStorageWordUniform(bufferIndex, baseWord + 5u, state.age);
    WriteStorageWordUniform(bufferIndex, baseWord + 6u, state.classification);
    WriteStorageWordUniform(bufferIndex, baseWord + 7u, floatBitsToUint(max(state.luminanceChangeEma, 0.0)));
}

SimpleDdgiProbeUpdate ReadSimpleDdgiProbeUpdate(uint bufferIndex, uint queueOffset)
{
    uint baseWord = queueOffset * SIMPLE_DDGI_PROBE_UPDATE_STRIDE_WORDS;
    uvec4 header = ReadStorageAlignedUVec4Uniform(bufferIndex, baseWord);
    SimpleDdgiProbeUpdate update;
    update.probeIndex = header.x;
    update.volumeIndex = header.y;
    update.flags = header.z;
    update.expectedGeneration = header.w;
    update.sourceRayCount = ReadStorageWordUniform(bufferIndex, baseWord + 4u);
    return update;
}

uint SimpleDdgiProbeGeneration(SimpleDdgiProbeState state)
{
    return (state.flags & SIMPLE_DDGI_PROBE_FLAG_GENERATION_MASK) >> SIMPLE_DDGI_PROBE_FLAG_GENERATION_SHIFT;
}

bool SimpleDdgiUpdateMatchesProbeGeneration(SimpleDdgiProbeUpdate update, SimpleDdgiProbeState state)
{
    uint expectedGeneration = update.expectedGeneration & SIMPLE_DDGI_UPDATE_GENERATION_MASK;
    return expectedGeneration != 0u && expectedGeneration == SimpleDdgiProbeGeneration(state);
}

void MarkSimpleDdgiProbeSourceCacheInvalid(uint bufferIndex, uint probeIndex)
{
    // Several rays of the same probe can discover the failed cache entry at
    // once. Use an atomic OR rather than a read/modify/write of the state
    // record so this recovery request cannot race relocation/classification.
    atomicOr(
        BindlessStorageBuffers[nonuniformEXT(bufferIndex)].Words[
            SimpleDdgiProbeStateBase(probeIndex) + 4u],
        SIMPLE_DDGI_PROBE_FLAG_SOURCE_CACHE_INVALID);
}

void ClearSimpleDdgiProbeSourceCacheInvalid(uint bufferIndex, uint probeIndex)
{
    atomicAnd(
        BindlessStorageBuffers[nonuniformEXT(bufferIndex)].Words[
            SimpleDdgiProbeStateBase(probeIndex) + 4u],
        ~SIMPLE_DDGI_PROBE_FLAG_SOURCE_CACHE_INVALID);
}

uint SimpleDdgiUpdateAge(SimpleDdgiProbeUpdate update)
{
    return max((update.expectedGeneration & SIMPLE_DDGI_UPDATE_AGE_MASK) >> SIMPLE_DDGI_UPDATE_AGE_SHIFT, 1u);
}

bool SimpleDdgiUpdateIsMaintenance(SimpleDdgiProbeUpdate update)
{
    return (update.flags & SIMPLE_DDGI_UPDATE_MAINTENANCE) != 0u;
}

bool SimpleDdgiUpdateRequiresSourceRefresh(SimpleDdgiProbeUpdate update)
{
    return (update.flags & SIMPLE_DDGI_UPDATE_SOURCE_REFRESH) != 0u;
}

uint SimpleDdgiUpdateRayCount(SimpleDdgiProbeUpdate update, SimpleDdgiParams p)
{
    uint packed = (update.flags & SIMPLE_DDGI_UPDATE_RAY_COUNT_MASK) >> SIMPLE_DDGI_UPDATE_RAY_COUNT_SHIFT;
    return clamp(packed == 0u ? p.raysPerProbe : packed, 1u, min(p.raysPerProbe, SIMPLE_DDGI_MAX_RAYS_PER_PROBE));
}

uint SimpleDdgiUpdateSourceRayCount(SimpleDdgiProbeUpdate update, SimpleDdgiParams p)
{
    uint fallback = SimpleDdgiUpdateRayCount(update, p);
    return clamp(
        update.sourceRayCount == 0u ? fallback : update.sourceRayCount,
        1u,
        min(p.raysPerProbe, SIMPLE_DDGI_MAX_RAYS_PER_PROBE));
}

int SimpleDdgiUpdateMaterialTextureMaxCascade(SimpleDdgiProbeUpdate update)
{
    uint packed = (update.flags & SIMPLE_DDGI_UPDATE_MATERIAL_TEXTURE_CASCADE_MASK) >> SIMPLE_DDGI_UPDATE_MATERIAL_TEXTURE_CASCADE_SHIFT;
    return int(packed) - 1;
}

uint SimpleDdgiUpdateMaxShadedLights(SimpleDdgiProbeUpdate update, uint fallback)
{
    uint packed = (update.flags & SIMPLE_DDGI_UPDATE_MAX_SHADED_LIGHTS_MASK) >> SIMPLE_DDGI_UPDATE_MAX_SHADED_LIGHTS_SHIFT;
    return packed == 0u ? fallback : min(packed - 1u, fallback);
}

SimpleDdgiParams ReadSimpleDdgiParams(uint bufferIndex)
{
    SimpleDdgiParams p;
    vec4 originAndSpacing = ReadStorageAlignedVec4Uniform(bufferIndex, 0u);
    vec4 grid = ReadStorageAlignedVec4Uniform(bufferIndex, 4u);
    vec4 atlas = ReadStorageAlignedVec4Uniform(bufferIndex, 8u);
    vec4 hysteresis = ReadStorageAlignedVec4Uniform(bufferIndex, 12u);
    vec4 environment = ReadStorageAlignedVec4Uniform(bufferIndex, 16u);
    vec4 updateRange = ReadStorageAlignedVec4Uniform(bufferIndex, 20u);
    vec4 debugAndBias = ReadStorageAlignedVec4Uniform(bufferIndex, 24u);
    vec4 rotation = ReadStorageAlignedVec4Uniform(bufferIndex, 28u);
    vec4 bias = ReadStorageAlignedVec4Uniform(bufferIndex, 32u);
    vec4 reserved = ReadStorageAlignedVec4Uniform(bufferIndex, 36u);
    vec4 biasLimits = ReadStorageAlignedVec4Uniform(bufferIndex, 40u);
    vec4 transportIndices = ReadStorageAlignedVec4Uniform(bufferIndex, 44u);
    vec4 transportControls = ReadStorageAlignedVec4Uniform(bufferIndex, 48u);
    p.origin = originAndSpacing.xyz;
    p.spacing = max(originAndSpacing.w, 0.001);
    p.gridCount = uvec3(max(grid.xyz, vec3(1.0)));
    p.probeCount = uint(max(grid.w, 0.0));
    p.irradianceTexels = clamp(uint(atlas.x), 1u, SIMPLE_DDGI_IRRADIANCE_TEXELS);
    p.visibilityTexels = clamp(uint(atlas.y), 1u, SIMPLE_DDGI_VISIBILITY_TEXELS);
    p.raysPerProbe = max(uint(atlas.z), 1u);
    p.farFieldResolution = max(uint(atlas.w), 1u);
    p.hysteresis = clamp(hysteresis.x, 0.0, 0.995);
    p.frameIndex = floatBitsToUint(hysteresis.y);
    p.flags = floatBitsToUint(hysteresis.z);
    p.farFieldStartDistance = max(hysteresis.w, 0.0);
    p.environmentRadiance = max(environment.xyz, vec3(0.0));
    p.environmentIntensity = max(environment.w, 0.0);
    p.environmentFallbackIntensity = clamp(updateRange.w, 0.0, 4.0);
    p.probesToUpdate = uint(max(updateRange.y, 0.0));
    p.debugView = uint(max(debugAndBias.x, 0.0));
    p.selfShadowBiasScale = max(debugAndBias.y, 0.0);
    p.indirectIntensity = max(debugAndBias.z, 0.0);
    p.farFieldMaxTraceSteps = max(uint(debugAndBias.w), 1u);
    p.rayRotation = dot(rotation, rotation) > 0.000001 ? normalize(rotation) : vec4(0.0, 0.0, 0.0, 1.0);
    p.normalBias = max(bias.x, 0.0);
    p.viewBias = max(bias.y, 0.0);
    p.hysteresisChangeThreshold = max(bias.z, 0.001);
    p.hysteresisStepThreshold = max(max(bias.w, 0.001), p.hysteresisChangeThreshold);
    p.volumeCount = min(uint(max(max(reserved.x, updateRange.z), 0.0)), SIMPLE_DDGI_MAX_VOLUME_COUNT);
    p.sampledAtlasLayersPerTexture = uint(max(reserved.y, 0.0));
    p.sampledAtlasTextureGroupCount = uint(max(reserved.z, 0.0));
    p.sampledAtlasEnabled = uint(max(reserved.w, 0.0));
    p.secondVolumeOwnershipEarlyOutThreshold = float(
        (p.flags & SIMPLE_DDGI_SECOND_VOLUME_OWNERSHIP_THRESHOLD_MASK) >>
        SIMPLE_DDGI_SECOND_VOLUME_OWNERSHIP_THRESHOLD_SHIFT) / 255.0;
    p.maximumWorldBias = clamp(biasLimits.x, 0.004, 1.0);
    p.architecturalThickness = clamp(biasLimits.y, 0.008, 4.0);
    p.thinWallLeakClampStrength = clamp(biasLimits.z, 0.0, 1.0);
    p.publishedIrradianceAtlasBufferIndex = floatBitsToUint(transportIndices.x);
    p.transportTargetIrradianceAtlasBufferIndex = floatBitsToUint(transportIndices.y);
    p.transportSourceCacheBufferIndex = floatBitsToUint(transportIndices.z);
    p.transportGeneration = floatBitsToUint(transportIndices.w);
    p.transportSolverRelaxation = clamp(transportControls.x, 0.05, 1.0);
    p.transportAlbedoClamp = clamp(transportControls.y, 0.50, 0.99);
    p.transportResidualThreshold = clamp(transportControls.z, 0.001, 1.0);
    p.transportMaximumSolverGenerations = clamp(uint(max(transportControls.w, 1.0)), 1u, 64u);
    return p;
}

SimpleDdgiVolume ReadSimpleDdgiVolume(uint bufferIndex, uint volumeIndex)
{
    uint baseWord = SIMPLE_DDGI_HEADER_WORDS + volumeIndex * SIMPLE_DDGI_VOLUME_STRIDE_WORDS;
    vec4 originAndSpacing = ReadStorageAlignedVec4Uniform(bufferIndex, baseWord + 0u);
    vec4 gridAndFirst = ReadStorageAlignedVec4Uniform(bufferIndex, baseWord + 4u);
    vec4 worldMinAndEdge = ReadStorageAlignedVec4Uniform(bufferIndex, baseWord + 8u);
    vec4 worldMaxAndKind = ReadStorageAlignedVec4Uniform(bufferIndex, baseWord + 12u);
    vec4 updateRange = ReadStorageAlignedVec4Uniform(bufferIndex, baseWord + 16u);
    vec4 raysAndReserved = ReadStorageAlignedVec4Uniform(bufferIndex, baseWord + 20u);

    SimpleDdgiVolume volume;
    volume.origin = originAndSpacing.xyz;
    volume.spacing = max(originAndSpacing.w, 0.001);
    volume.gridCount = uvec3(max(gridAndFirst.xyz, vec3(1.0)));
    volume.firstProbeIndex = uint(max(gridAndFirst.w, 0.0));
    volume.worldMin = worldMinAndEdge.xyz;
    volume.edgeFadeDistance = max(worldMinAndEdge.w, volume.spacing);
    volume.worldMax = worldMaxAndKind.xyz;
    volume.kind = uint(max(worldMaxAndKind.w, 0.0));
    volume.probesToUpdate = uint(max(updateRange.y, 0.0));
    volume.sourceOrdinal = uint(max(raysAndReserved.x, 0.0));
    volume.physicalOffset = uvec3(max(raysAndReserved.yzw, vec3(0.0)));
    return volume;
}

uint SimpleDdgiVolumeQualityCascade(SimpleDdgiVolume volume)
{
    if (volume.kind == SIMPLE_DDGI_VOLUME_KIND_AUTHORED)
        return SIMPLE_DDGI_AUTHORED_VOLUME_CASCADE;
    if (volume.kind != SIMPLE_DDGI_VOLUME_KIND_RING || volume.sourceOrdinal < 10000u)
        return 0u;
    return min(volume.sourceOrdinal - 10000u, 3u);
}

bool SimpleDdgiContains(SimpleDdgiVolume volume, vec3 worldPosition)
{
    return all(greaterThanEqual(worldPosition, volume.worldMin)) &&
        all(lessThanEqual(worldPosition, volume.worldMax));
}

float SimpleDdgiEdgeWeight(SimpleDdgiVolume volume, vec3 worldPosition)
{
    vec3 distanceToFace = min(worldPosition - volume.worldMin, volume.worldMax - worldPosition);
    float edgeDistance = min(min(distanceToFace.x, distanceToFace.y), distanceToFace.z);
    return smoothstep(0.0, max(volume.edgeFadeDistance, 0.001), edgeDistance);
}

uint SimpleDdgiVolumeProbeCount(SimpleDdgiVolume volume)
{
    return volume.gridCount.x * volume.gridCount.y * volume.gridCount.z;
}

uint SimpleDdgiProbeIndex(uvec3 coord, SimpleDdgiParams p)
{
    return coord.x + coord.y * p.gridCount.x + coord.z * p.gridCount.x * p.gridCount.y;
}

uint SimpleDdgiProbeIndex(uvec3 coord, SimpleDdgiVolume volume)
{
    // Ring coordinates are logical/world-relative.  Atlas/state storage is
    // toroidal, so preserved cells retain their physical slot without a copy.
    uvec3 physical = (coord + volume.physicalOffset) % max(volume.gridCount, uvec3(1u));
    return volume.firstProbeIndex + physical.x + physical.y * volume.gridCount.x + physical.z * volume.gridCount.x * volume.gridCount.y;
}

uvec3 SimpleDdgiProbeCoord(uint probeIndex, SimpleDdgiParams p)
{
    uint xy = max(p.gridCount.x * p.gridCount.y, 1u);
    uint z = probeIndex / xy;
    uint rem = probeIndex - z * xy;
    uint y = rem / max(p.gridCount.x, 1u);
    uint x = rem - y * max(p.gridCount.x, 1u);
    return uvec3(x, y, z);
}

vec3 SimpleDdgiProbeWorldPosition(uint probeIndex, SimpleDdgiParams p)
{
    return p.origin + vec3(SimpleDdgiProbeCoord(probeIndex, p)) * p.spacing;
}

bool ResolveSimpleDdgiProbeVolume(uint globalProbeIndex, SimpleDdgiParams p, out SimpleDdgiVolume volume, out uint localProbeIndex)
{
    for (uint volumeIndex = 0u; volumeIndex < p.volumeCount; volumeIndex++)
    {
        SimpleDdgiVolume candidate = ReadSimpleDdgiVolume(uint(SIMPLE_DDGI_PARAMS_BUFFER_INDEX), volumeIndex);
        uint count = SimpleDdgiVolumeProbeCount(candidate);
        if (globalProbeIndex >= candidate.firstProbeIndex && globalProbeIndex < candidate.firstProbeIndex + count)
        {
            volume = candidate;
            localProbeIndex = globalProbeIndex - candidate.firstProbeIndex;
            return true;
        }
    }

    volume = ReadSimpleDdgiVolume(uint(SIMPLE_DDGI_PARAMS_BUFFER_INDEX), 0u);
    localProbeIndex = 0u;
    return false;
}

uvec3 SimpleDdgiProbeCoord(uint localProbeIndex, SimpleDdgiVolume volume)
{
    uint xy = max(volume.gridCount.x * volume.gridCount.y, 1u);
    uint z = localProbeIndex / xy;
    uint rem = localProbeIndex - z * xy;
    uint y = rem / max(volume.gridCount.x, 1u);
    uint x = rem - y * max(volume.gridCount.x, 1u);
    uvec3 physical = uvec3(x, y, z);
    return (physical + volume.gridCount - (volume.physicalOffset % volume.gridCount)) % volume.gridCount;
}

vec3 SimpleDdgiProbeWorldPosition(uint globalProbeIndex, SimpleDdgiParams p, out uint volumeIndexOut)
{
    for (uint volumeIndex = 0u; volumeIndex < p.volumeCount; volumeIndex++)
    {
        SimpleDdgiVolume volume = ReadSimpleDdgiVolume(uint(SIMPLE_DDGI_PARAMS_BUFFER_INDEX), volumeIndex);
        uint count = SimpleDdgiVolumeProbeCount(volume);
        if (globalProbeIndex >= volume.firstProbeIndex && globalProbeIndex < volume.firstProbeIndex + count)
        {
            volumeIndexOut = volumeIndex;
            return volume.origin + vec3(SimpleDdgiProbeCoord(globalProbeIndex - volume.firstProbeIndex, volume)) * volume.spacing;
        }
    }

    volumeIndexOut = 0u;
    return SimpleDdgiProbeWorldPosition(globalProbeIndex, p);
}

vec3 SimpleDdgiProbeLogicalPosition(SimpleDdgiVolume volume, uint localProbeIndex)
{
    return volume.origin + vec3(SimpleDdgiProbeCoord(localProbeIndex, volume)) * volume.spacing;
}

vec3 SimpleDdgiProbeRelocatedPosition(uint probeIndex, SimpleDdgiVolume volume, uint localProbeIndex)
{
    SimpleDdgiProbeState state = ReadSimpleDdgiProbeState(uint(SIMPLE_DDGI_PROBE_STATE_BUFFER_INDEX), probeIndex);
    return SimpleDdgiProbeLogicalPosition(volume, localProbeIndex) + state.relocation;
}

vec2 SimpleDdgiOctEncode(vec3 n)
{
    n /= max(abs(n.x) + abs(n.y) + abs(n.z), 0.000001);
    vec2 encoded = n.xy;
    if (n.z < 0.0)
        encoded = (1.0 - abs(encoded.yx)) * sign(encoded.xy);
    return encoded * 0.5 + 0.5;
}

vec3 SimpleDdgiOctDecode(vec2 e)
{
    vec2 f = e * 2.0 - 1.0;
    vec3 n = vec3(f.x, f.y, 1.0 - abs(f.x) - abs(f.y));
    float t = clamp(-n.z, 0.0, 1.0);
    n.xy += vec2(n.x >= 0.0 ? -t : t, n.y >= 0.0 ? -t : t);
    return normalize(n);
}

vec3 SimpleDdgiRotateByQuaternion(vec3 v, vec4 q)
{
    return v + 2.0 * cross(q.xyz, cross(q.xyz, v) + q.w * v);
}

vec4 SimpleDdgiMultiplyQuaternions(vec4 left, vec4 right)
{
    return vec4(
        left.w * right.xyz + right.w * left.xyz + cross(left.xyz, right.xyz),
        left.w * right.w - dot(left.xyz, right.xyz));
}

uint SimpleDdgiHash(uint value)
{
    value ^= value >> 16u;
    value *= 0x7feb352du;
    value ^= value >> 15u;
    value *= 0x846ca68bu;
    value ^= value >> 16u;
    return value;
}

float SimpleDdgiHashToUnitFloat(uint value, uint salt)
{
    return float(SimpleDdgiHash(value ^ salt) >> 8u) * (1.0 / 16777216.0);
}

// Compose a stable, uniform rotation for every probe with the frame rotation.
// This preserves per-probe temporal sampling while preventing all probes from
// moving through the same Monte-Carlo error pattern together.
vec4 SimpleDdgiPerProbeRayRotation(uint probeIndex, vec4 frameRotation)
{
    float u1 = SimpleDdgiHashToUnitFloat(probeIndex, 0x9e3779b9u);
    float u2 = SimpleDdgiHashToUnitFloat(probeIndex, 0x7f4a7c15u);
    float u3 = SimpleDdgiHashToUnitFloat(probeIndex, 0x94d049bbu);
    float r1 = sqrt(max(0.0, 1.0 - u1));
    float r2 = sqrt(max(0.0, u1));
    float theta1 = 2.0 * SIMPLE_DDGI_PI * u2;
    float theta2 = 2.0 * SIMPLE_DDGI_PI * u3;
    vec4 probeRotation = vec4(
        r1 * sin(theta1), r1 * cos(theta1),
        r2 * sin(theta2), r2 * cos(theta2));
    return normalize(SimpleDdgiMultiplyQuaternions(frameRotation, probeRotation));
}

vec3 SimpleDdgiFibonacciDirection(uint rayIndex, uint rayCount, vec4 rayRotation)
{
    float i = float(rayIndex);
    float n = max(float(rayCount), 1.0);
    float golden = 2.399963229728653;
    float z = 1.0 - 2.0 * (i + 0.5) / n;
    float radius = sqrt(max(0.0, 1.0 - z * z));
    float angle = golden * i;
    return normalize(SimpleDdgiRotateByQuaternion(vec3(cos(angle) * radius, sin(angle) * radius, z), rayRotation));
}

uint SimpleDdgiAtlasWord(uint probeIndex, uint texelIndex, uint texelsPerProbe)
{
    return (probeIndex * texelsPerProbe * texelsPerProbe + texelIndex) * 2u;
}

// Persistent V2 source cache: one entry per physical probe slot and
// deterministic full-sequence ray direction.  `directionRayIndex`, rather than
// the queue-local maintenance ray index, is the key so a low-rate maintenance
// subset reuses exactly the source ray that was originally traced.
const uint SIMPLE_DDGI_TRANSPORT_RAY_CACHE_STRIDE_WORDS = 9u;
const uint SIMPLE_DDGI_TRANSPORT_CACHE_VALID_FLAG = 1u << 24u;
const uint SIMPLE_DDGI_TRANSPORT_CACHE_HIT_FLAG = 1u << 25u;
const uint SIMPLE_DDGI_TRANSPORT_CACHE_BACKFACE_FLAG = 1u << 26u;
// Forward debug ABI value. Kept here because this include is parsed before the
// forward shader declares its public debug-view constants.
const uint SIMPLE_DDGI_DEBUG_SOURCE_CACHE_RADIANCE = 125u;

struct SimpleDdgiTransportRayCache
{
    vec3 sourceRadiance;
    float distance;
    vec3 direction;
    vec3 normal;
    vec3 diffuseReflectance;
    vec3 transmittedDiffuseReflectance;
    float materialOcclusion;
    uint generationAndFlags;
};

uint SimpleDdgiTransportRayCacheBase(uint probeIndex, uint directionRayIndex, SimpleDdgiParams p)
{
    return (probeIndex * p.raysPerProbe + directionRayIndex) *
        SIMPLE_DDGI_TRANSPORT_RAY_CACHE_STRIDE_WORDS;
}

uint PackSimpleDdgiTransportOctDirection(vec3 direction)
{
    vec2 oct = SimpleDdgiOctEncode(normalize(direction)) * 2.0 - 1.0;
    return packSnorm2x16(clamp(oct, vec2(-1.0), vec2(1.0)));
}

vec3 UnpackSimpleDdgiTransportOctDirection(uint packed)
{
    vec2 oct = unpackSnorm2x16(packed) * 0.5 + 0.5;
    return SimpleDdgiOctDecode(clamp(oct, vec2(0.0), vec2(1.0)));
}

void WriteSimpleDdgiTransportRayCache(
    uint bufferIndex,
    uint probeIndex,
    uint directionRayIndex,
    SimpleDdgiParams p,
    vec3 sourceRadiance,
    float distance,
    vec3 direction,
    vec3 normal,
    vec3 diffuseReflectance,
    vec3 transmittedDiffuseReflectance,
    float materialOcclusion,
    float hitKind,
    uint probeGeneration)
{
    uint baseWord = SimpleDdgiTransportRayCacheBase(probeIndex, directionRayIndex, p);
    WriteStorageVec4Uniform(
        bufferIndex,
        baseWord,
        vec4(clamp(sourceRadiance, vec3(0.0), vec3(65504.0)), max(distance, 0.0)));
    WriteStorageWordUniform(bufferIndex, baseWord + 4u, PackSimpleDdgiTransportOctDirection(direction));
    WriteStorageWordUniform(bufferIndex, baseWord + 5u, PackSimpleDdgiTransportOctDirection(normal));
    WriteStorageWordUniform(
        bufferIndex,
        baseWord + 6u,
        packUnorm4x8(vec4(
            clamp(diffuseReflectance, vec3(0.0), vec3(1.0)),
            clamp(materialOcclusion, 0.0, 1.0))));
    WriteStorageWordUniform(
        bufferIndex,
        baseWord + 7u,
        packUnorm4x8(vec4(
            clamp(transmittedDiffuseReflectance, vec3(0.0), vec3(1.0)),
            0.0)));
    uint flags = SIMPLE_DDGI_TRANSPORT_CACHE_VALID_FLAG |
        (hitKind >= 0.5 ? SIMPLE_DDGI_TRANSPORT_CACHE_HIT_FLAG : 0u) |
        (hitKind > 1.5 ? SIMPLE_DDGI_TRANSPORT_CACHE_BACKFACE_FLAG : 0u);
    WriteStorageWordUniform(
        bufferIndex,
        baseWord + 8u,
        (probeGeneration & SIMPLE_DDGI_UPDATE_GENERATION_MASK) | flags);
}

bool ReadSimpleDdgiTransportRayCache(
    uint bufferIndex,
    uint probeIndex,
    uint directionRayIndex,
    SimpleDdgiParams p,
    uint expectedProbeGeneration,
    out SimpleDdgiTransportRayCache cache)
{
    cache.sourceRadiance = vec3(0.0);
    cache.distance = 0.0;
    cache.direction = vec3(0.0, 1.0, 0.0);
    cache.normal = vec3(0.0, 1.0, 0.0);
    cache.diffuseReflectance = vec3(0.0);
    cache.transmittedDiffuseReflectance = vec3(0.0);
    cache.materialOcclusion = 1.0;
    cache.generationAndFlags = 0u;
    if (bufferIndex == 0u || directionRayIndex >= p.raysPerProbe)
        return false;

    uint baseWord = SimpleDdgiTransportRayCacheBase(probeIndex, directionRayIndex, p);
    uint generationAndFlags = ReadStorageWordUniform(bufferIndex, baseWord + 8u);
    if ((generationAndFlags & SIMPLE_DDGI_TRANSPORT_CACHE_VALID_FLAG) == 0u ||
        (generationAndFlags & SIMPLE_DDGI_UPDATE_GENERATION_MASK) !=
            (expectedProbeGeneration & SIMPLE_DDGI_UPDATE_GENERATION_MASK))
    {
        return false;
    }

    vec4 sourceRadianceDistance = ReadStorageVec4Uniform(bufferIndex, baseWord);
    cache.sourceRadiance = max(sourceRadianceDistance.rgb, vec3(0.0));
    cache.distance = max(sourceRadianceDistance.w, 0.0);
    uvec4 surface = ReadStorageUVec4Uniform(bufferIndex, baseWord + 4u);
    cache.direction = UnpackSimpleDdgiTransportOctDirection(surface.x);
    cache.normal = UnpackSimpleDdgiTransportOctDirection(surface.y);
    vec4 packedSurface = unpackUnorm4x8(surface.z);
    cache.diffuseReflectance = packedSurface.rgb;
    cache.transmittedDiffuseReflectance =
        unpackUnorm4x8(surface.w).rgb;
    cache.materialOcclusion = packedSurface.a;
    cache.generationAndFlags = generationAndFlags;
    return true;
}

bool SimpleDdgiTransportRayCacheIsHit(SimpleDdgiTransportRayCache cache)
{
    return (cache.generationAndFlags & SIMPLE_DDGI_TRANSPORT_CACHE_HIT_FLAG) != 0u;
}

float SimpleDdgiTransportRayCacheHitKind(SimpleDdgiTransportRayCache cache)
{
    if (!SimpleDdgiTransportRayCacheIsHit(cache))
        return 0.0;
    return (cache.generationAndFlags & SIMPLE_DDGI_TRANSPORT_CACHE_BACKFACE_FLAG) != 0u
        ? 2.0
        : 1.0;
}

vec4 ReadSimpleDdgiAtlasTexel(uint bufferIndex, uint probeIndex, uint texelIndex, uint texelsPerProbe)
{
    uint word = SimpleDdgiAtlasWord(probeIndex, texelIndex, texelsPerProbe);
    vec2 xy = unpackHalf2x16(ReadStorageWordUniform(bufferIndex, word));
    vec2 zw = unpackHalf2x16(ReadStorageWordUniform(bufferIndex, word + 1u));
    return vec4(xy, zw);
}

void WriteSimpleDdgiAtlasTexel(uint bufferIndex, uint probeIndex, uint texelIndex, uint texelsPerProbe, vec4 value)
{
    value = clamp(value, vec4(0.0), vec4(65504.0));
    uint word = SimpleDdgiAtlasWord(probeIndex, texelIndex, texelsPerProbe);
    WriteStorageWordUniform(bufferIndex, word, packHalf2x16(value.xy));
    WriteStorageWordUniform(bufferIndex, word + 1u, packHalf2x16(value.zw));
}

uint SimpleDdgiDirectionTexel(vec3 direction, uint texelsPerProbe)
{
    vec2 uv = clamp(SimpleDdgiOctEncode(direction), vec2(0.0), vec2(0.999999));
    uvec2 xy = uvec2(floor(uv * float(texelsPerProbe)));
    xy = min(xy, uvec2(texelsPerProbe - 1u));
    return xy.x + xy.y * texelsPerProbe;
}

uint SimpleDdgiMirrorOctTexelIndex(ivec2 coord, uint texelsPerProbe)
{
    int n = int(texelsPerProbe);
    ivec2 c = coord;
    if (c.x < 0)
    {
        c.x = -c.x - 1;
        c.y = n - 1 - c.y;
    }
    else if (c.x >= n)
    {
        c.x = 2 * n - c.x - 1;
        c.y = n - 1 - c.y;
    }

    if (c.y < 0)
    {
        c.y = -c.y - 1;
        c.x = n - 1 - c.x;
    }
    else if (c.y >= n)
    {
        c.y = 2 * n - c.y - 1;
        c.x = n - 1 - c.x;
    }

    c = clamp(c, ivec2(0), ivec2(n - 1));
    return uint(c.x) + uint(c.y) * texelsPerProbe;
}

bool SimpleDdgiCanSampleAtlasImage(SimpleDdgiParams p, uint bufferIndex, uint probeIndex)
{
    if (SIMPLE_DDGI_DISABLE_SAMPLED_ATLAS != 0 ||
        p.sampledAtlasEnabled == 0u ||
        p.sampledAtlasLayersPerTexture == 0u ||
        p.sampledAtlasTextureGroupCount == 0u ||
        (bufferIndex != uint(SIMPLE_DDGI_IRRADIANCE_ATLAS_BUFFER_INDEX) &&
         bufferIndex != uint(SIMPLE_DDGI_VISIBILITY_ATLAS_BUFFER_INDEX)))
    {
        return false;
    }

    uint groupIndex = probeIndex / p.sampledAtlasLayersPerTexture;
    return groupIndex < p.sampledAtlasTextureGroupCount &&
        groupIndex < uint(SIMPLE_DDGI_SAMPLED_ATLAS_TEXTURE_GROUP_COUNT);
}

vec4 SampleSimpleDdgiAtlasImage(
    SimpleDdgiParams p,
    uint bufferIndex,
    uint probeIndex,
    vec2 encodedDirection)
{
    uint groupIndex = probeIndex / p.sampledAtlasLayersPerTexture;
    uint layerIndex = probeIndex - groupIndex * p.sampledAtlasLayersPerTexture;
    int textureIndex = bufferIndex == uint(SIMPLE_DDGI_IRRADIANCE_ATLAS_BUFFER_INDEX)
        ? SIMPLE_DDGI_SAMPLED_IRRADIANCE_TEXTURE_BASE_INDEX + int(groupIndex)
        : SIMPLE_DDGI_SAMPLED_VISIBILITY_TEXTURE_BASE_INDEX + int(groupIndex);
    return texture(
        BindlessArrayTextures[nonuniformEXT(textureIndex)],
        vec3(clamp(encodedDirection, vec2(0.0), vec2(1.0)), float(layerIndex)));
}

vec4 SampleSimpleDdgiAtlasBilinear(
    uint bufferIndex,
    uint probeIndex,
    vec3 direction,
    uint texelsPerProbe,
    SimpleDdgiParams p)
{
    vec2 encodedDirection = SimpleDdgiOctEncode(direction);
    vec2 texelUv = encodedDirection * float(texelsPerProbe) - vec2(0.5);
    ivec2 base = ivec2(floor(texelUv));
    vec2 f = fract(texelUv);
    // Hardware filtering is exactly equivalent for a strictly interior quad.
    // At octahedral seams retain the SSBO mirror lookup, which preserves the
    // established cross-edge convention instead of clamping the image border.
    if (all(greaterThanEqual(base, ivec2(0))) &&
        all(lessThan(base + ivec2(1), ivec2(int(texelsPerProbe)))) &&
        SimpleDdgiCanSampleAtlasImage(p, bufferIndex, probeIndex))
    {
        return SampleSimpleDdgiAtlasImage(p, bufferIndex, probeIndex, encodedDirection);
    }

    vec4 s00 = ReadSimpleDdgiAtlasTexel(bufferIndex, probeIndex, SimpleDdgiMirrorOctTexelIndex(base, texelsPerProbe), texelsPerProbe);
    vec4 s10 = ReadSimpleDdgiAtlasTexel(bufferIndex, probeIndex, SimpleDdgiMirrorOctTexelIndex(base + ivec2(1, 0), texelsPerProbe), texelsPerProbe);
    vec4 s01 = ReadSimpleDdgiAtlasTexel(bufferIndex, probeIndex, SimpleDdgiMirrorOctTexelIndex(base + ivec2(0, 1), texelsPerProbe), texelsPerProbe);
    vec4 s11 = ReadSimpleDdgiAtlasTexel(bufferIndex, probeIndex, SimpleDdgiMirrorOctTexelIndex(base + ivec2(1, 1), texelsPerProbe), texelsPerProbe);
    return mix(mix(s00, s10, f.x), mix(s01, s11, f.x), f.y);
}

float SimpleDdgiChebyshev(float mean, float mean2, float receiverDistance, float probeSpacing)
{
    if (receiverDistance <= mean)
        return 1.0;
    // Fine lattices need enough numerical uncertainty to tolerate the 0.15-0.3 m
    // mean-distance error common around trim and relocated probes.  Cap the
    // spacing term at four metres: coarse-ring uncertainty must remain large
    // enough to avoid losing every supported probe to numerical over-occlusion.
    // Measured variance always wins and the mean-distance bound remains a final
    // guard for probes whose recorded visibility distance is very small.
    float measuredVariance = max(mean2 - mean * mean, 0.0);
    float varianceSpacing = min(max(probeSpacing, 0.0), SIMPLE_DDGI_VISIBILITY_VARIANCE_SPACING_CAP);
    float spacingFloor = max(0.0005, varianceSpacing * varianceSpacing * 0.005);
    float meanBound = max(0.0005, mean * mean * 0.0625);
    float varianceFloor = min(spacingFloor, meanBound);
    float variance = max(measuredVariance, varianceFloor);
    float d = receiverDistance - mean;
    return clamp(variance / (variance + d * d), 0.0, 1.0);
}

float SimpleDdgiVisibilitySelectionWeight(float transportVisibility)
{
    // Cubing the Chebyshev bound is the standard DDGI confidence shaping. The
    // old 0.01..0.08 smoothstep granted full selection authority to a probe
    // with only eight-percent visibility, so a weak cross-wall residual was
    // normalized back into a full-strength irradiance sample.
    float visibility = clamp(transportVisibility, 0.0, 1.0);
    return max(
        visibility * visibility * visibility,
        SIMPLE_DDGI_VISIBILITY_SELECTION_FLOOR);
}

vec3 SimpleDdgiBiasedSamplePosition(vec3 worldPos, vec3 normal, vec3 viewDir, SimpleDdgiParams p, float volumeSpacing)
{
    vec3 safeNormal = length(normal) > 0.00001 ? normalize(normal) : vec3(0.0, 1.0, 0.0);
    vec3 safeView = length(viewDir) > 0.00001 ? normalize(viewDir) : safeNormal;
    // Settings are expressed as spacing-relative scales, but spacing is never
    // the only limit. The combined displacement is capped in world space and
    // by a conservative fraction of the declared architectural thickness so a
    // coarse far ring cannot push a lookup through a thin wall.
    float spacing = max(volumeSpacing, 0.001);
    float thicknessCap = max(0.002, p.architecturalThickness * 0.25);
    float totalBiasCap = min(p.maximumWorldBias, thicknessCap);
    float normalCap = min(totalBiasCap, max(0.002, spacing * 0.20));
    float normalBias = clamp(p.normalBias * spacing, 0.002, normalCap);
    float viewCap = min(max(totalBiasCap - normalBias, 0.0), max(0.0, spacing * 0.35));
    float viewBias = clamp(p.viewBias * spacing, 0.0, viewCap);
    return worldPos + safeNormal * normalBias + safeView * viewBias;
}

vec3 SimpleDdgiBiasedSamplePosition(vec3 worldPos, vec3 normal, vec3 viewDir, SimpleDdgiParams p)
{
    return SimpleDdgiBiasedSamplePosition(worldPos, normal, viewDir, p, p.spacing);
}

bool SelectSimpleDdgiVolume(SimpleDdgiParams p, vec3 worldPosition, out uint selectedVolumeIndex, out SimpleDdgiVolume selectedVolume, out float selectedEdgeWeight)
{
    for (uint volumeIndex = 0u; volumeIndex < SIMPLE_DDGI_MAX_SELECTION_VOLUME_CHECKS; volumeIndex++)
    {
        if (volumeIndex >= p.volumeCount)
            break;
        SimpleDdgiVolume volume = ReadSimpleDdgiVolume(uint(SIMPLE_DDGI_PARAMS_BUFFER_INDEX), volumeIndex);
        if (!SimpleDdgiContains(volume, worldPosition))
            continue;

        selectedVolumeIndex = volumeIndex;
        selectedVolume = volume;
        selectedEdgeWeight = SimpleDdgiEdgeWeight(volume, worldPosition);
        return true;
    }

    selectedVolumeIndex = 0u;
    selectedVolume = ReadSimpleDdgiVolume(uint(SIMPLE_DDGI_PARAMS_BUFFER_INDEX), 0u);
    selectedEdgeWeight = 0.0;
    return false;
}

// Finds metadata for the next containing fallback. This intentionally performs
// no atlas/probe reads. The caller may invoke it a second time only after a
// sampled fallback leaves a receiver below the recovery support threshold.
bool FindSimpleDdgiFallbackVolume(
    SimpleDdgiParams p,
    uint selectedVolumeIndex,
    vec3 worldPosition,
    out uint fallbackVolumeIndex,
    out SimpleDdgiVolume fallbackVolume)
{
    for (uint candidateOffset = 1u;
         candidateOffset <= SIMPLE_DDGI_MAX_GATHER_FALLBACK_CANDIDATE_CHECKS;
         candidateOffset++)
    {
        uint candidateIndex = selectedVolumeIndex + candidateOffset;
        if (candidateIndex >= p.volumeCount)
            break;

        SimpleDdgiVolume candidate = ReadSimpleDdgiVolume(
            uint(SIMPLE_DDGI_PARAMS_BUFFER_INDEX),
            candidateIndex);
        // Candidate identity is always evaluated from the unbiased receiver
        // position. View/normal bias is an interpolation detail only.
        if (!SimpleDdgiContains(candidate, worldPosition))
            continue;

        fallbackVolumeIndex = candidateIndex;
        fallbackVolume = candidate;
        return true;
    }

    fallbackVolumeIndex = 0u;
    fallbackVolume = ReadSimpleDdgiVolume(uint(SIMPLE_DDGI_PARAMS_BUFFER_INDEX), 0u);
    return false;
}


struct SimpleDdgiGatherResult
{
    vec3 irradiance;
    // Direct + emissive + sky source cache evaluated independently of recursive
    // bounce, using the same probe/cascade weights as the receiver gather.
    vec3 sourceCacheIrradiance;
    // Weighted with the same directional masses that compose irradiance. This
    // lets the cascade-selection debug view show every volume that contributed,
    // rather than only the finest volume selected before fallback blending.
    vec3 contributingVolumeColor;
    // Fraction of spatial interpolation mass backed by state-valid probe data.
    // Probe-to-receiver visibility is deliberately excluded: it selects which
    // valid probes represent irradiance, but it does not make their atlas data
    // unavailable.
    float validSupport;
    float directionalSupport;
    float spatialCoverage;
    float transportVisibility;
    float ownership;
    uint selectedVolume;
    uint secondaryVolume;
    float selectedSpacing;
    float transitionWeight;
    float secondVolumeUsed;
    float primaryContributionWeight;
    float secondaryContributionWeight;
    uint validProbeCount;
    uint combinedRejectionMask;
    uint firstRejectionReason;
    uint rejectedProbeCount;
};

SimpleDdgiGatherResult EmptySimpleDdgiGatherResult()
{
    SimpleDdgiGatherResult result;
    result.irradiance = vec3(0.0);
    result.sourceCacheIrradiance = vec3(0.0);
    result.contributingVolumeColor = vec3(0.0);
    result.validSupport = 0.0;
    result.directionalSupport = 0.0;
    result.spatialCoverage = 0.0;
    result.transportVisibility = 0.0;
    result.ownership = 0.0;
    result.selectedVolume = 0u;
    result.secondaryVolume = SIMPLE_DDGI_INVALID_VOLUME_INDEX;
    result.selectedSpacing = 0.0;
    result.transitionWeight = 1.0;
    result.secondVolumeUsed = 0.0;
    result.primaryContributionWeight = 0.0;
    result.secondaryContributionWeight = 0.0;
    result.validProbeCount = 0u;
    result.combinedRejectionMask = 0u;
    result.firstRejectionReason = SIMPLE_DDGI_GATHER_REJECTION_REASON_COUNT;
    result.rejectedProbeCount = 0u;
    return result;
}

vec3 SimpleDdgiVolumeContributorDebugColor(uint volumeIndex, uint volumeKind)
{
    // Keep this palette synchronized with the CPU probe-volume overlay so the
    // shaded contributor view and probe markers identify the same volumes.
    if (volumeKind == SIMPLE_DDGI_VOLUME_KIND_AUTHORED)
        return vec3(0.95, 0.90, 0.25);

    uint paletteIndex = volumeIndex % 3u;
    if (paletteIndex == 0u)
        return vec3(0.20, 0.75, 1.00);
    if (paletteIndex == 1u)
        return vec3(0.30, 0.95, 0.55);
    return vec3(0.95, 0.30, 0.85);
}

float SimpleDdgiRadiometricOwnership(SimpleDdgiGatherResult gather)
{
    // The gathered irradiance is normalized independently of support, but
    // ownership is not binary.  Granting an entire cell to a single usable
    // trilinear corner turns a bright isolated probe into full authority and
    // hides the environment complement. Data availability is intentionally
    // independent of probe-to-receiver visibility: visibility already selects
    // the representative probes in the normalized gather and the late leak
    // attenuation remains responsible for genuinely all-blocked cells.
    // Directional support is a separate geometric authority term. A cell can
    // have complete, valid atlas data while every probe lies behind the receiver;
    // that estimator must not publish zero irradiance at full ownership.
    float spatialCoverage = clamp(gather.spatialCoverage, 0.0, 1.0);
    float validSupport = clamp(gather.validSupport, 0.0, 1.0);
    float directionalSupport = clamp(gather.directionalSupport, 0.0, 1.0);
    float availabilityAuthority = smoothstep(
        0.0,
        SIMPLE_DDGI_OWNERSHIP_SUPPORT_RAMP,
        validSupport);
    float directionalAuthority = smoothstep(
        0.0,
        SIMPLE_DDGI_OWNERSHIP_DIRECTIONAL_SUPPORT_RAMP,
        directionalSupport);
    return spatialCoverage * availabilityAuthority * directionalAuthority;
}

float SimpleDdgiDirectionalGatherAuthority(SimpleDdgiGatherResult gather)
{
    return smoothstep(
        0.0,
        SIMPLE_DDGI_OWNERSHIP_DIRECTIONAL_SUPPORT_RAMP,
        clamp(gather.directionalSupport, 0.0, 1.0));
}

float SimpleDdgiLeakAttenuation(SimpleDdgiGatherResult gather, SimpleDdgiParams p)
{
    // Visibility participates in normalized probe selection, so normalizing the
    // gathered irradiance removes its absolute magnitude. That is desirable for
    // ordinary interpolation, but it lets a cell whose every probe is behind a
    // wall promote a tiny residual weight back to full radiance. Convert the
    // existing admission interval into a bounded confidence only at composition
    // time. The authored thin-wall control determines how much of that confidence
    // is applied; disabling the policy leaves the normalized estimator unchanged.
    float visibilityConfidence = smoothstep(
        SIMPLE_DDGI_VISIBILITY_SELECTION_LOW,
        SIMPLE_DDGI_VISIBILITY_SELECTION_HIGH,
        clamp(gather.transportVisibility, 0.0, 1.0));
    return clamp(
        mix(1.0, visibilityConfidence, p.thinWallLeakClampStrength),
        0.05,
        1.0);
}

uint SimpleDdgiProbeGatherRejectionMask(SimpleDdgiProbeState state, vec4 irradiance, vec4 visibility);

bool SimpleDdgiProbeSupportsGather(SimpleDdgiProbeState state, vec4 irradiance, vec4 visibility)
{
    return SimpleDdgiProbeGatherRejectionMask(state, irradiance, visibility) == 0u;
}

// Returns the reason mask as well as the legacy Boolean decision. Keeping this
// pure makes it usable by diagnostic builds without adding work to production
// gather paths.
uint SimpleDdgiProbeGatherRejectionMask(SimpleDdgiProbeState state, vec4 irradiance, vec4 visibility)
{
    uint mask = 0u;
    if ((state.flags & SIMPLE_DDGI_PROBE_FLAG_FRESH) != 0u) mask |= SIMPLE_DDGI_GATHER_REJECT_FRESH;
    if ((state.flags & SIMPLE_DDGI_PROBE_FLAG_SCROLL_EXPOSED) != 0u) mask |= SIMPLE_DDGI_GATHER_REJECT_SCROLL_EXPOSED;
    if ((state.flags & SIMPLE_DDGI_PROBE_FLAG_RELOCATION_PENDING) != 0u) mask |= SIMPLE_DDGI_GATHER_REJECT_RELOCATION_PENDING;
    if ((state.flags & SIMPLE_DDGI_PROBE_FLAG_INACTIVE) != 0u) mask |= SIMPLE_DDGI_GATHER_REJECT_INACTIVE_FLAG;
    if (state.classification == SIMPLE_DDGI_CLASSIFICATION_INACTIVE) mask |= SIMPLE_DDGI_GATHER_REJECT_INACTIVE_CLASSIFICATION;
    if (state.activeWeight <= 0.001) mask |= SIMPLE_DDGI_GATHER_REJECT_ZERO_ACTIVE_WEIGHT;
    if (irradiance.w <= 0.5) mask |= SIMPLE_DDGI_GATHER_REJECT_INVALID_IRRADIANCE;
    if (visibility.z <= 0.5) mask |= SIMPLE_DDGI_GATHER_REJECT_INVALID_VISIBILITY;
    return mask;
}

void RecordSimpleDdgiGatherRejectionMask(
    SimpleDdgiParams p,
    uint gatherRole,
    uint rejectionMask)
{
    if (!SimpleDdgiDetailedDiagnosticsEnabled(p) || rejectionMask == 0u)
        return;

    uint safeRole = min(gatherRole, SIMPLE_DDGI_GATHER_ROLE_COUNT - 1u);
    uint roleBase = SIMPLE_DDGI_GATHER_REJECTION_COUNTER_BASE +
        safeRole * SIMPLE_DDGI_GATHER_REJECTION_REASON_COUNT;
    uint diagnosticFrame = p.frameIndex % uint(FRAMES_IN_FLIGHT);
    for (uint reason = 0u; reason < SIMPLE_DDGI_GATHER_REJECTION_REASON_COUNT; reason++)
    {
        if ((rejectionMask & (1u << reason)) != 0u)
            AddSimpleDdgiGatherDiagnostic(p, diagnosticFrame, roleBase + reason);
    }
}

vec3 SampleSimpleDdgiProbeSourceCacheIrradiance(
    SimpleDdgiParams p,
    uint probeIndex,
    SimpleDdgiProbeState state,
    vec3 safeNormal)
{
    if ((p.flags & SIMPLE_DDGI_FLAG_TRANSPORT_V2) == 0u ||
        p.transportSourceCacheBufferIndex == 0u)
    {
        return vec3(0.0);
    }

    vec3 accumulated = vec3(0.0);
    float weightSum = 0.0;
    uint rayCount = min(p.raysPerProbe, SIMPLE_DDGI_MAX_RAYS_PER_PROBE);
    uint probeGeneration = SimpleDdgiProbeGeneration(state);
    for (uint rayIndex = 0u; rayIndex < rayCount; rayIndex++)
    {
        SimpleDdgiTransportRayCache source;
        if (!ReadSimpleDdgiTransportRayCache(
                p.transportSourceCacheBufferIndex,
                probeIndex,
                rayIndex,
                p,
                probeGeneration,
                source))
        {
            continue;
        }

        float weight = max(dot(safeNormal, normalize(source.direction)), 0.0);
        accumulated += clamp(source.sourceRadiance, vec3(0.0), vec3(65504.0)) * weight;
        weightSum += weight;
    }

    // Match the irradiance estimator used by ddgi_simple_blend.comp so this
    // differs from the published atlas only by recursive bounce and solver
    // history, not by a debug-only radiometric convention.
    return weightSum > 0.000001
        ? accumulated * (SIMPLE_DDGI_PI / weightSum)
        : vec3(0.0);
}

SimpleDdgiGatherResult SampleSimpleDdgiVolumeGather(
    SimpleDdgiParams p,
    SimpleDdgiVolume volume,
    uint volumeIndex,
    uint gatherRole,
    vec3 biasedWorldPos,
    vec3 safeNormal)
{
    SimpleDdgiGatherResult result = EmptySimpleDdgiGatherResult();
    result.selectedVolume = volumeIndex;
    result.secondaryVolume = SIMPLE_DDGI_INVALID_VOLUME_INDEX;
    result.selectedSpacing = volume.spacing;
    vec3 grid = (biasedWorldPos - volume.origin) / volume.spacing;
    vec3 baseF = floor(grid);
    // Keep the interpolation affine inside each cell. Applying smoothstep to
    // the grid fraction creates probe-centred plateaus followed by a 1.5x
    // steeper transition at the cell midpoint, which exposes small per-probe
    // visibility differences as a repeating light pattern on planar surfaces.
    vec3 fracV = clamp(grid - baseF, vec3(0.0), vec3(1.0));
    ivec3 base = ivec3(baseF);
    vec3 accumulated = vec3(0.0);
    vec3 sourceCacheAccumulated = vec3(0.0);
    float availableMass = 0.0;
    float validMass = 0.0;
    float directionalMass = 0.0;
    float visibleMass = 0.0;

    for (uint z = 0u; z < 2u; z++)
    for (uint y = 0u; y < 2u; y++)
    for (uint x = 0u; x < 2u; x++)
    {
        ivec3 c = base + ivec3(int(x), int(y), int(z));
        if (any(lessThan(c, ivec3(0))) || any(greaterThanEqual(c, ivec3(volume.gridCount))))
        {
            result.combinedRejectionMask |= SIMPLE_DDGI_GATHER_REJECT_OUTSIDE_DOMAIN;
            if (result.firstRejectionReason >= SIMPLE_DDGI_GATHER_REJECTION_REASON_COUNT)
                result.firstRejectionReason = 8u;
            result.rejectedProbeCount++;
            RecordSimpleDdgiGatherRejectionMask(
                p,
                gatherRole,
                SIMPLE_DDGI_GATHER_REJECT_OUTSIDE_DOMAIN);
            continue;
        }

        vec3 w3 = mix(1.0 - fracV, fracV, vec3(x, y, z));
        float trilinear = w3.x * w3.y * w3.z;
        result.spatialCoverage += trilinear;

        uint probeIndex = SimpleDdgiProbeIndex(uvec3(c), volume);
        SimpleDdgiProbeState state = ReadSimpleDdgiProbeState(uint(SIMPLE_DDGI_PROBE_STATE_BUFFER_INDEX), probeIndex);
        vec3 probePos = volume.origin + vec3(c) * volume.spacing + state.relocation;
        vec3 toSurface = biasedWorldPos - probePos;
        float distanceToProbe = length(toSurface);
        vec3 probeToSurface = distanceToProbe > 0.00001 ? toSurface / distanceToProbe : safeNormal;
        vec4 irradiance = SampleSimpleDdgiAtlasBilinear(p.publishedIrradianceAtlasBufferIndex, probeIndex, safeNormal, p.irradianceTexels, p);
        vec4 moments = SampleSimpleDdgiAtlasBilinear(uint(SIMPLE_DDGI_VISIBILITY_ATLAS_BUFFER_INDEX), probeIndex, probeToSurface, p.visibilityTexels, p);
        uint rejectionMask = SimpleDdgiProbeGatherRejectionMask(state, irradiance, moments);
        if (rejectionMask != 0u)
        {
            result.combinedRejectionMask |= rejectionMask;
            if (result.firstRejectionReason >= SIMPLE_DDGI_GATHER_REJECTION_REASON_COUNT)
                result.firstRejectionReason = uint(findLSB(rejectionMask));
            result.rejectedProbeCount++;
            RecordSimpleDdgiGatherRejectionMask(p, gatherRole, rejectionMask);
            continue;
        }

        float halfLambert = clamp(dot(safeNormal, -probeToSurface) * 0.5 + 0.5, 0.0, 1.0);
        // Directional weighting chooses representative probes; it is not missing
        // data and must not remove energy from an otherwise valid interpolation
        // cell.  Keep a tiny directional floor so an entirely back-facing cell can
        // still produce a stable estimate while visibility remains authoritative.
        float directionalWeight = max(halfLambert * halfLambert, 1.0e-4);
        float dataWeight = trilinear * clamp(state.activeWeight, 0.0, 1.0);
        float directionalTransportWeight = dataWeight * directionalWeight;
        float visibilityBias = clamp(0.03 * p.selfShadowBiasScale * volume.spacing, 0.002, volume.spacing * 0.10);
        float transportVisibility = SimpleDdgiChebyshev(
            moments.x,
            moments.y,
            max(distanceToProbe - visibilityBias, 0.0),
            volume.spacing);
        float visibilitySelectionWeight =
            SimpleDdgiVisibilitySelectionWeight(transportVisibility);
        float selectedDataWeight = dataWeight * visibilitySelectionWeight;
        float selectedDirectionalWeight = directionalTransportWeight * visibilitySelectionWeight;
        accumulated += max(irradiance.rgb, vec3(0.0)) * selectedDirectionalWeight;
        if (p.debugView == SIMPLE_DDGI_DEBUG_SOURCE_CACHE_RADIANCE)
        {
            sourceCacheAccumulated += SampleSimpleDdgiProbeSourceCacheIrradiance(
                p,
                probeIndex,
                state,
                safeNormal) * selectedDirectionalWeight;
        }
        availableMass += dataWeight;
        validMass += selectedDataWeight;
        directionalMass += selectedDirectionalWeight;
        visibleMass += selectedDirectionalWeight * transportVisibility;
        result.validProbeCount++;
    }

    float spatialCoverage = clamp(result.spatialCoverage, 0.0, 1.0);
    result.spatialCoverage = spatialCoverage;
    result.validSupport = spatialCoverage > 0.000001
        ? clamp(availableMass / spatialCoverage, 0.0, 1.0)
        : 0.0;
    // Keep selection mass separate from data availability. It remains the
    // cascade/fallback interpolation authority and retains visibility shaping.
    result.directionalSupport = validMass > 0.000001
        ? clamp(directionalMass / validMass, 0.0, 1.0)
        : 0.0;
    result.transportVisibility = directionalMass > 0.000001
        ? clamp(visibleMass / directionalMass, 0.0, 1.0)
        : 0.0;
    result.ownership = clamp(validMass, 0.0, 1.0);
    // Visibility is an interpolation/reliability weight. Normalizing it out is
    // the standard DDGI estimator and prevents probe-to-receiver visibility from
    // becoming a second ambient-occlusion term. Physical shadowing is already in
    // the radiance traced into each probe.
    result.irradiance = result.validProbeCount > 0u && directionalMass > 0.000001
        ? clamp(accumulated / directionalMass, vec3(0.0), vec3(64.0))
        : vec3(0.0);
    result.sourceCacheIrradiance = result.validProbeCount > 0u && directionalMass > 0.000001
        ? clamp(sourceCacheAccumulated / directionalMass, vec3(0.0), vec3(64.0))
        : vec3(0.0);
    result.contributingVolumeColor = result.validProbeCount > 0u && directionalMass > 0.000001
        ? SimpleDdgiVolumeContributorDebugColor(volumeIndex, volume.kind)
        : vec3(0.0);
    result.primaryContributionWeight = result.validProbeCount > 0u && directionalMass > 0.000001 ? 1.0 : 0.0;
    if (result.validProbeCount == 0u && result.spatialCoverage > 0.000001)
    {
        AddSimpleDdgiGatherDiagnostic(
            p,
            p.frameIndex % uint(FRAMES_IN_FLIGHT),
            SIMPLE_DDGI_GATHER_ALL_FAILED_COUNTER_BASE +
                min(gatherRole, SIMPLE_DDGI_GATHER_ROLE_COUNT - 1u));
    }
    return result;
}

// Volume identity and transition ownership are evaluated from the unbiased
// receiver position.  Bias is only an interpolation/visibility query detail.
// If bias would cross a selected domain, clamp the query to that volume's
// logical lattice and let the unbiased overlap policy request the fallback.
// This prevents a camera/view direction from changing which world-space field
// owns a receiver while retaining the self-intersection protection of bias.
vec3 SimpleDdgiResolveInterpolationPosition(
    SimpleDdgiVolume volume,
    vec3 worldPos,
    vec3 safeNormal,
    vec3 viewDir,
    SimpleDdgiParams p,
    out bool biasedOutsideSelectionDomain)
{
    vec3 biased = SimpleDdgiBiasedSamplePosition(
        worldPos,
        safeNormal,
        viewDir,
        p,
        volume.spacing);
    biasedOutsideSelectionDomain = !SimpleDdgiContains(volume, biased);
    vec3 latticeMaximum = volume.origin +
        vec3(max(volume.gridCount, uvec3(1u)) - uvec3(1u)) * volume.spacing;
    return clamp(biased, volume.origin, latticeMaximum);
}

vec3 SimpleDdgiEnvironmentIrradianceFallback(vec3 safeNormal, SimpleDdgiParams p)
{
    GPUEnvironmentData environment = ReadGiEnvironmentData();
    if (environment.Enabled != 0u &&
        (EnvironmentUsesAnalyticSky(environment) ||
            environment.IrradianceTextureIndex >= 0))
    {
        // This fallback feeds probe bounce/transport.  DiffuseIntensity is the
        // forward ambient-IBL complement and must not dim the sky seen by GI.
        return EvaluateEnvironmentTransportIrradiance(environment, safeNormal);
    }

    float skyWeight = clamp(safeNormal.y * 0.5 + 0.5, 0.0, 1.0);
    return max(p.environmentRadiance, vec3(0.0)) * p.environmentIntensity * skyWeight;
}

float EstimateFarFieldSkyVisibility(
    vec3 worldPos,
    vec3 surfaceNormal,
    SimpleDdgiParams simpleParams,
    uint diagnosticSampleWeight)
{
    FarFieldClipmapParams farField = ReadFarFieldClipmapParams(uint(FAR_FIELD_CLIPMAP_PARAMS_BUFFER_INDEX));
    if (!farField.enabled)
        return 1.0;

    vec3 safeNormal = length(surfaceNormal) > 0.00001
        ? normalize(surfaceNormal)
        : vec3(0.0, 1.0, 0.0);
    float safeVoxelSize = max(farField.voxelSize, 0.001);
    // Starting at a hit point biased by only a few centimetres leaves every
    // upward cone inside the coarse surface voxel.  Move into receiver-side air
    // by at least one voxel before testing the far-field sky gate.
    vec3 traceOrigin = worldPos + safeNormal * max(safeVoxelSize, 0.03);

    const vec3 coneDirections[3] = vec3[](
        vec3(0.0, 1.0, 0.0),
        normalize(vec3(0.70710678, 0.70710678, 0.0)),
        normalize(vec3(-0.5, 0.70710678, 0.5))
    );

    uint coneCount = 3u;
    float maxDistance = FarFieldTraceMaximumDistance(farField);
    float visibility = 0.0;
    for (uint i = 0u; i < coneCount; i++)
    {
        float hitT;
        vec3 hitNormal;
        vec3 hitAlbedo;
        bool stepExhausted;
        uint visitedSteps;
        bool blocked = TraceFarFieldClipmapDetailed(
            traceOrigin,
            coneDirections[i],
            safeVoxelSize * 0.5,
            maxDistance,
            hitT,
            hitNormal,
            hitAlbedo,
            stepExhausted,
            visitedSteps);
        visibility += blocked ? 0.0 : 1.0;
    }

    float normalizedVisibility = clamp(visibility / float(coneCount), 0.0, 1.0);
    float effectiveVisibility = max(normalizedVisibility, SIMPLE_DDGI_MINIMUM_SKY_VISIBILITY);
    if (diagnosticSampleWeight != 0u)
    {
        // Fragment callers use a 16x16 sparse sample and pass a weight of 256.
        // The counters therefore retain their estimated full-frame meaning while
        // replacing millions of fully contended atomics with a few thousand.
        uint diagnosticFrame = simpleParams.frameIndex % uint(FRAMES_IN_FLIGHT);
        AddSimpleDdgiDiagnostic(
            simpleParams,
            diagnosticFrame,
            DDGI_INVESTIGATION_SKY_VISIBILITY_SAMPLE_COUNTER,
            diagnosticSampleWeight);
        AddSimpleDdgiDiagnostic(
            simpleParams,
            diagnosticFrame,
            DDGI_INVESTIGATION_SKY_VISIBILITY_ACCUM_COUNTER,
            uint(clamp(effectiveVisibility, 0.0, 16.0) * 1024.0 + 0.5) *
                diagnosticSampleWeight);
    }
    return effectiveVisibility;
}

uint ReadSimpleDdgiFlags(uint bufferIndex)
{
    return ReadStorageWordUniform(bufferIndex, SIMPLE_DDGI_FLAGS_WORD);
}

SimpleDdgiGatherResult BlendSimpleDdgiGatherResults(
    SimpleDdgiGatherResult outer,
    SimpleDdgiGatherResult inner,
    float innerWeight)
{
    float w = clamp(innerWeight, 0.0, 1.0);
    float innerValidMass = inner.ownership * w;
    // Inner data has priority, but a geometrically selected ring must not hide a
    // valid coarser ring when its own probes are fresh, inactive, or otherwise
    // unsupported. The outer ring fills only the ownership still unrepresented.
    float outerWeight = 1.0 - innerValidMass;
    float outerValidMass = outer.ownership * outerWeight;
    float validMass = outerValidMass + innerValidMass;
    float outerDirectionalMass = outerValidMass * outer.directionalSupport;
    float innerDirectionalMass = innerValidMass * inner.directionalSupport;
    float directionalMass = outerDirectionalMass + innerDirectionalMass;
    float outerVisibleMass = outerDirectionalMass * outer.transportVisibility;
    float innerVisibleMass = innerDirectionalMass * inner.transportVisibility;
    float visibleMass = outerVisibleMass + innerVisibleMass;
    vec3 accumulated = outer.irradiance * outerDirectionalMass +
        inner.irradiance * innerDirectionalMass;
    vec3 sourceCacheAccumulated = outer.sourceCacheIrradiance * outerDirectionalMass +
        inner.sourceCacheIrradiance * innerDirectionalMass;
    vec3 contributorColorAccumulated = outer.contributingVolumeColor * outerDirectionalMass +
        inner.contributingVolumeColor * innerDirectionalMass;
    SimpleDdgiGatherResult result;
    float innerSpatialMass = inner.spatialCoverage * w;
    float outerSpatialMass = outer.spatialCoverage * (1.0 - innerSpatialMass);
    result.spatialCoverage = clamp(
        innerSpatialMass + outerSpatialMass,
        0.0,
        1.0);
    // Availability follows every field that can actually contribute. Keep this
    // independent from visibility-selected `validMass`, which still decides how
    // inner and outer irradiance are combined and whether recovery is required.
    float innerAvailableMass = inner.validSupport * inner.spatialCoverage * w;
    float outerAvailableMass = outer.validSupport * outer.spatialCoverage * outerWeight;
    float availableMass = innerAvailableMass + outerAvailableMass;
    result.validSupport = result.spatialCoverage > 0.000001
        ? clamp(availableMass / result.spatialCoverage, 0.0, 1.0)
        : 0.0;
    result.directionalSupport = validMass > 0.000001
        ? clamp(directionalMass / validMass, 0.0, 1.0)
        : 0.0;
    result.transportVisibility = directionalMass > 0.000001
        ? clamp(visibleMass / directionalMass, 0.0, 1.0)
        : 0.0;
    result.ownership = clamp(validMass, 0.0, 1.0);
    result.irradiance = directionalMass > 0.000001
        ? clamp(accumulated / directionalMass, vec3(0.0), vec3(64.0))
        : vec3(0.0);
    result.sourceCacheIrradiance = directionalMass > 0.000001
        ? clamp(sourceCacheAccumulated / directionalMass, vec3(0.0), vec3(64.0))
        : vec3(0.0);
    result.contributingVolumeColor = directionalMass > 0.000001
        ? clamp(contributorColorAccumulated / directionalMass, vec3(0.0), vec3(1.0))
        : vec3(0.0);
    result.selectedVolume = inner.selectedVolume;
    result.secondaryVolume = outer.selectedVolume;
    result.transitionWeight = w;
    result.primaryContributionWeight = directionalMass > 0.000001
        ? clamp(innerDirectionalMass / directionalMass, 0.0, 1.0)
        : 0.0;
    result.secondaryContributionWeight = directionalMass > 0.000001
        ? clamp(outerDirectionalMass / directionalMass, 0.0, 1.0)
        : 0.0;
    result.selectedSpacing = mix(
        outer.selectedSpacing,
        inner.selectedSpacing,
        result.primaryContributionWeight);
    result.secondVolumeUsed = result.secondaryContributionWeight > 0.000001 ? 1.0 : 0.0;
    result.validProbeCount = inner.validProbeCount + outer.validProbeCount;
    result.combinedRejectionMask = inner.combinedRejectionMask | outer.combinedRejectionMask;
    result.firstRejectionReason =
        inner.firstRejectionReason < SIMPLE_DDGI_GATHER_REJECTION_REASON_COUNT
            ? inner.firstRejectionReason
            : outer.firstRejectionReason;
    result.rejectedProbeCount = inner.rejectedProbeCount + outer.rejectedProbeCount;
    return result;
}

SimpleDdgiGatherResult SampleSimpleDdgiGather(
    SimpleDdgiParams p,
    vec3 worldPos,
    vec3 normal,
    vec3 viewDir)
{
    SimpleDdgiGatherResult empty = EmptySimpleDdgiGatherResult();
    if ((p.flags & (SIMPLE_DDGI_FLAG_ENABLED | SIMPLE_DDGI_FLAG_STRUCTURED_GATHER_ENABLED)) !=
            (SIMPLE_DDGI_FLAG_ENABLED | SIMPLE_DDGI_FLAG_STRUCTURED_GATHER_ENABLED) ||
        p.probeCount == 0u || p.volumeCount == 0u)
        return empty;

    vec3 safeNormal = length(normal) > 0.00001 ? normalize(normal) : vec3(0.0, 1.0, 0.0);
    uint selectedVolumeIndex = 0u;
    SimpleDdgiVolume selectedVolume;
    float edgeWeight = 0.0;
    if (!SelectSimpleDdgiVolume(p, worldPos, selectedVolumeIndex, selectedVolume, edgeWeight))
    {
        RecordSimpleDdgiGatherRejectionMask(
            p,
            SIMPLE_DDGI_GATHER_ROLE_PRIMARY,
            SIMPLE_DDGI_GATHER_REJECT_OUTSIDE_DOMAIN);
        return empty;
    }
    // Keep edge ownership in stable receiver space.  The old implementation
    // recomputed this using the biased position, so reversing view direction at
    // the same receiver could toggle a second cascade and change mean energy.
    bool selectedBiasOutsideSelectionDomain;
    vec3 selectedInterpolationPosition = SimpleDdgiResolveInterpolationPosition(
        selectedVolume,
        worldPos,
        safeNormal,
        viewDir,
        p,
        selectedBiasOutsideSelectionDomain);

    SimpleDdgiGatherResult selected = SampleSimpleDdgiVolumeGather(
        p,
        selectedVolume,
        selectedVolumeIndex,
        SIMPLE_DDGI_GATHER_ROLE_PRIMARY,
        selectedInterpolationPosition,
        safeNormal);
    uint diagnosticFrame = p.frameIndex % uint(FRAMES_IN_FLIGHT);
    AddSimpleDdgiGatherDiagnostic(
        p,
        diagnosticFrame,
        DDGI_INVESTIGATION_SIMPLE_GATHER_COUNTER);
    AddSimpleDdgiGatherDiagnostic(
        p,
        diagnosticFrame,
        DDGI_INVESTIGATION_SIMPLE_VOLUME_PRIMARY_GATHER_COUNTER_BASE + selectedVolumeIndex);
    AddSimpleDdgiGatherDiagnostic(
        p,
        diagnosticFrame,
        DDGI_INVESTIGATION_SIMPLE_VOLUME_SAMPLED_GATHER_COUNTER_BASE + selectedVolumeIndex);
    // Bound fallback work by probe-field ownership, not by receiver visibility
    // or the normal-facing interpolation weight. Those terms select a stable
    // estimate inside one field; treating them as missing coverage made almost
    // every interior receiver sample a coarser ring as well. A second gather is
    // still required at a ring edge, after a bias-domain crossing, or when the
    // primary field lacks the configured fraction of valid probe data.
    float selectedDirectionalAuthority = SimpleDdgiDirectionalGatherAuthority(selected);
    bool selectedDirectionalSupportUsable =
        selected.directionalSupport >= SIMPLE_DDGI_OWNERSHIP_DIRECTIONAL_SUPPORT_RAMP;
    float selectedTransitionOwnership =
        selectedDirectionalSupportUsable ? selected.validSupport * edgeWeight : 0.0;
    if (!selectedBiasOutsideSelectionDomain &&
        selectedTransitionOwnership >= p.secondVolumeOwnershipEarlyOutThreshold)
    {
        AddSimpleDdgiGatherDiagnostic(
            p,
            diagnosticFrame,
            SIMPLE_DDGI_ONE_GATHER_PIXEL_COUNTER);
        selected.spatialCoverage *= edgeWeight;
        selected.ownership *= edgeWeight;
        selected.transitionWeight = edgeWeight;
        return selected;
    }

    uint fallbackVolumeIndex = 0u;
    SimpleDdgiVolume fallbackVolume;
    bool foundFallback = FindSimpleDdgiFallbackVolume(
        p,
        selectedVolumeIndex,
        worldPos,
        fallbackVolumeIndex,
        fallbackVolume);
    if (foundFallback)
    {
        // Select one attribution before gathering. Recovery can replace this
        // classification below, but no receiver can contribute to two reason
        // buckets for the same second-volume event.
        uint secondGatherReasonCounter =
            SIMPLE_DDGI_SECOND_GATHER_DEBUG_ONLY_COUNTER;
        if (selectedBiasOutsideSelectionDomain)
        {
            secondGatherReasonCounter =
                SIMPLE_DDGI_SECOND_GATHER_COVERAGE_EDGE_COUNTER;
        }
        else if (selected.validProbeCount == 0u || selected.validSupport <= 0.000001)
        {
            secondGatherReasonCounter =
                SIMPLE_DDGI_SECOND_GATHER_MISSING_INVALID_PRIMARY_COUNTER;
        }
        else if (edgeWeight < 0.999)
        {
            secondGatherReasonCounter =
                SIMPLE_DDGI_SECOND_GATHER_RING_TRANSITION_COUNTER;
        }
        else if (selectedTransitionOwnership <
                 p.secondVolumeOwnershipEarlyOutThreshold)
        {
            secondGatherReasonCounter =
                SIMPLE_DDGI_SECOND_GATHER_OWNERSHIP_BELOW_COUNTER;
        }

        bool fallbackBiasOutsideSelectionDomain;
        vec3 fallbackInterpolationPosition = SimpleDdgiResolveInterpolationPosition(
            fallbackVolume,
            worldPos,
            safeNormal,
            viewDir,
            p,
            fallbackBiasOutsideSelectionDomain);

        // This counter measures receivers that enter the fallback path. A rare
        // third recovery sample below does not increment it again, so the ratio
        // remains bounded to one fallback event per primary gather.
        SimpleDdgiGatherResult fallback = SampleSimpleDdgiVolumeGather(
            p,
            fallbackVolume,
            fallbackVolumeIndex,
            SIMPLE_DDGI_GATHER_ROLE_FALLBACK,
            fallbackInterpolationPosition,
            safeNormal);
        AddSimpleDdgiGatherDiagnostic(
            p,
            diagnosticFrame,
            DDGI_INVESTIGATION_SIMPLE_SECOND_VOLUME_GATHER_COUNTER);
        AddSimpleDdgiGatherDiagnostic(
            p,
            diagnosticFrame,
            DDGI_INVESTIGATION_SIMPLE_VOLUME_SAMPLED_GATHER_COUNTER_BASE + fallbackVolumeIndex);
        // A fine cell with valid atlas data but no forward-facing corner must
        // not hide a usable coarser cell. Directional authority changes only
        // cascade composition; data availability remains independently visible
        // through validSupport and the DataConfidence debug view.
        float selectedGatherWeight = edgeWeight * selectedDirectionalAuthority;
        SimpleDdgiGatherResult combined = BlendSimpleDdgiGatherResults(
            fallback,
            selected,
            selectedGatherWeight);

        // A dense fine lattice can place all eight local probes inside roof/wall
        // geometry. If the normal two-volume path still has only trace ownership,
        // let one containing coarser ring fill that missing mass instead of
        // promoting the tiny remainder to full radiometric authority.
        uint recoveryBaseVolumeIndex = fallbackVolumeIndex;
        bool recoveryGathered = false;
        for (uint recoverySample = 0u;
             recoverySample < SIMPLE_DDGI_MAX_RECOVERY_GATHER_SAMPLES;
             recoverySample++)
        {
            if (combined.ownership >= SIMPLE_DDGI_OWNERSHIP_SUPPORT_RAMP &&
                combined.directionalSupport >= SIMPLE_DDGI_OWNERSHIP_DIRECTIONAL_SUPPORT_RAMP)
                break;
            uint recoveryVolumeIndex = 0u;
            SimpleDdgiVolume recoveryVolume;
            bool foundRecovery = FindSimpleDdgiFallbackVolume(
                p,
                recoveryBaseVolumeIndex,
                worldPos,
                recoveryVolumeIndex,
                recoveryVolume);
            if (!foundRecovery)
                break;
            recoveryBaseVolumeIndex = recoveryVolumeIndex;
            {
                bool recoveryBiasOutsideSelectionDomain;
                vec3 recoveryInterpolationPosition = SimpleDdgiResolveInterpolationPosition(
                    recoveryVolume,
                    worldPos,
                    safeNormal,
                    viewDir,
                    p,
                    recoveryBiasOutsideSelectionDomain);
                SimpleDdgiGatherResult recovery = SampleSimpleDdgiVolumeGather(
                    p,
                    recoveryVolume,
                    recoveryVolumeIndex,
                    SIMPLE_DDGI_GATHER_ROLE_RECOVERY,
                    recoveryInterpolationPosition,
                    safeNormal);
                recoveryGathered = true;
                AddSimpleDdgiGatherDiagnostic(
                    p,
                    diagnosticFrame,
                    DDGI_INVESTIGATION_SIMPLE_VOLUME_SAMPLED_GATHER_COUNTER_BASE + recoveryVolumeIndex);
                float combinedDirectionalAuthority =
                    SimpleDdgiDirectionalGatherAuthority(combined);
                combined = BlendSimpleDdgiGatherResults(
                    recovery,
                    combined,
                    combinedDirectionalAuthority);
            }
        }

        AddSimpleDdgiGatherDiagnostic(
            p,
            diagnosticFrame,
            recoveryGathered
                ? SIMPLE_DDGI_RECOVERY_GATHER_PIXEL_COUNTER
                : SIMPLE_DDGI_TWO_GATHER_PIXEL_COUNTER);
        AddSimpleDdgiGatherDiagnostic(
            p,
            diagnosticFrame,
            recoveryGathered
                ? SIMPLE_DDGI_SECOND_GATHER_RECOVERY_COUNTER
                : secondGatherReasonCounter);

        return combined;
    }

    // At the outer edge only ownership fades.  Irradiance stays normalized so the
    // caller can compose the represented DDGI share and the missing environment
    // share exactly once.
    selected.spatialCoverage *= edgeWeight;
    selected.ownership *= edgeWeight;
    selected.transitionWeight = edgeWeight;
    AddSimpleDdgiGatherDiagnostic(
        p,
        diagnosticFrame,
        SIMPLE_DDGI_ONE_GATHER_PIXEL_COUNTER);
    return selected;
}

SimpleDdgiGatherResult SampleSimpleDdgiGather(vec3 worldPos, vec3 normal, vec3 viewDir)
{
    SimpleDdgiParams p = ReadSimpleDdgiParams(uint(SIMPLE_DDGI_PARAMS_BUFFER_INDEX));
    return SampleSimpleDdgiGather(p, worldPos, normal, viewDir);
}

vec3 SampleSimpleDdgiUnifiedIrradiance(
    SimpleDdgiParams p,
    vec3 worldPos,
    vec3 normal,
    vec3 viewDir,
    bool allowFallback,
    out float ownershipOut)
{
    if ((p.flags & SIMPLE_DDGI_FLAG_ENABLED) == 0u)
    {
        ownershipOut = 0.0;
        return vec3(0.0);
    }

    vec3 safeNormal = length(normal) > 0.00001 ? normalize(normal) : vec3(0.0, 1.0, 0.0);
    SimpleDdgiGatherResult gather = SampleSimpleDdgiGather(p, worldPos, safeNormal, viewDir);
    float radiometricOwnership = SimpleDdgiRadiometricOwnership(gather);
    float leakAttenuation = SimpleDdgiLeakAttenuation(gather, p);
    float effectiveOwnership = radiometricOwnership * leakAttenuation;
    ownershipOut = effectiveOwnership;
    vec3 irradiance = gather.irradiance * effectiveOwnership;
    if (allowFallback)
    {
        // A visibility-rejected DDGI share is intentionally dark. Only genuinely
        // missing probe ownership receives the environment complement; otherwise
        // a wall leak would merely be replaced by unoccluded sky irradiance.
        float fallbackWeight = (1.0 - radiometricOwnership) * p.environmentFallbackIntensity;
        if (fallbackWeight > SIMPLE_DDGI_ENVIRONMENT_FALLBACK_MIN_WEIGHT)
        {
            // Environment fallback represents the receiver's missing diffuse
            // ownership. It is intentionally evaluated at stable receiver space,
            // not at a view-biased probe query position, so reversing the camera
            // cannot change diffuse fallback energy at a fixed world point.
            vec3 fallback = SimpleDdgiEnvironmentIrradianceFallback(safeNormal, p);
            if ((p.flags & SIMPLE_DDGI_FLAG_SKY_VISIBILITY_ENABLED) != 0u)
                fallback *= EstimateFarFieldSkyVisibility(worldPos, safeNormal, p, 1u);
            irradiance += fallback * fallbackWeight;
        }
    }

    return clamp(irradiance, vec3(0.0), vec3(64.0));
}

vec3 SampleSimpleDdgiUnifiedIrradiance(
    vec3 worldPos,
    vec3 normal,
    vec3 viewDir,
    bool allowFallback,
    out float ownershipOut)
{
    SimpleDdgiParams p = ReadSimpleDdgiParams(uint(SIMPLE_DDGI_PARAMS_BUFFER_INDEX));
    return SampleSimpleDdgiUnifiedIrradiance(
        p,
        worldPos,
        normal,
        viewDir,
        allowFallback,
        ownershipOut);
}

vec3 SampleSimpleDdgiUnifiedIrradiance(
    SimpleDdgiParams p,
    vec3 worldPos,
    vec3 normal,
    vec3 viewDir,
    bool allowFallback)
{
    float ignoredOwnership;
    return SampleSimpleDdgiUnifiedIrradiance(
        p,
        worldPos,
        normal,
        viewDir,
        allowFallback,
        ignoredOwnership);
}

vec3 SampleSimpleDdgiUnifiedIrradiance(vec3 worldPos, vec3 normal, vec3 viewDir, bool allowFallback)
{
    float ignoredOwnership;
    return SampleSimpleDdgiUnifiedIrradiance(worldPos, normal, viewDir, allowFallback, ignoredOwnership);
}

vec3 SampleSimpleDdgiSolverBounceIrradiance(
    SimpleDdgiParams p,
    vec3 worldPos,
    vec3 normal,
    vec3 viewDir,
    out float solverOwnershipOut,
    out float fallbackWeightOut)
{
    if ((p.flags & SIMPLE_DDGI_FLAG_ENABLED) == 0u)
    {
        solverOwnershipOut = 0.0;
        fallbackWeightOut = 0.0;
        return vec3(0.0);
    }

    vec3 safeNormal = length(normal) > 0.00001 ? normalize(normal) : vec3(0.0, 1.0, 0.0);
    SimpleDdgiGatherResult gather = SampleSimpleDdgiGather(p, worldPos, safeNormal, viewDir);
    float spatialCoverage = clamp(gather.spatialCoverage, 0.0, 1.0);
    float solverOwnership = spatialCoverage * smoothstep(
        0.0,
        SIMPLE_DDGI_SOLVER_OWNERSHIP_SUPPORT_RAMP,
        clamp(gather.validSupport, 0.0, 1.0));
    solverOwnershipOut = solverOwnership;
    // Probe-to-hit visibility already participates in normalized probe
    // selection. Applying the receiver leak clamp again here turns visibility
    // confidence into a per-bounce absorption coefficient (0.35^N in the
    // common low-confidence case), which prevents the Jacobi fixed point from
    // accumulating multi-hop energy. Thin-wall rejection remains in vis^3
    // selection and, independently, at the final receiver.
    vec3 irradiance = gather.irradiance * solverOwnership;

    float fallbackWeight = (1.0 - solverOwnership) * p.environmentFallbackIntensity;
    fallbackWeightOut = fallbackWeight;
    if (fallbackWeight > SIMPLE_DDGI_ENVIRONMENT_FALLBACK_MIN_WEIGHT)
    {
        // This is a fixed-point boundary condition, not receiver-visible sky.
        // Visibility-gating an ownership deficit makes missing coverage a
        // per-generation energy sink and prevents a conservative solve.
        vec3 fallback = SimpleDdgiEnvironmentIrradianceFallback(safeNormal, p);
        irradiance += fallback * fallbackWeight;
    }

    return clamp(irradiance, vec3(0.0), vec3(64.0));
}

vec3 SampleSimpleDdgiSolverBounceIrradiance(
    vec3 worldPos,
    vec3 normal,
    vec3 viewDir,
    out float solverOwnershipOut,
    out float fallbackWeightOut)
{
    SimpleDdgiParams p = ReadSimpleDdgiParams(uint(SIMPLE_DDGI_PARAMS_BUFFER_INDEX));
    return SampleSimpleDdgiSolverBounceIrradiance(
        p,
        worldPos,
        normal,
        viewDir,
        solverOwnershipOut,
        fallbackWeightOut);
}

vec3 SampleSimpleDdgiSolverBounceIrradiance(
    SimpleDdgiParams p,
    vec3 worldPos,
    vec3 normal,
    vec3 viewDir)
{
    float ignoredOwnership;
    float ignoredFallbackWeight;
    return SampleSimpleDdgiSolverBounceIrradiance(
        p,
        worldPos,
        normal,
        viewDir,
        ignoredOwnership,
        ignoredFallbackWeight);
}

vec3 SampleSimpleDdgiSolverBounceIrradiance(vec3 worldPos, vec3 normal, vec3 viewDir)
{
    float ignoredOwnership;
    float ignoredFallbackWeight;
    return SampleSimpleDdgiSolverBounceIrradiance(
        worldPos,
        normal,
        viewDir,
        ignoredOwnership,
        ignoredFallbackWeight);
}

SimpleDdgiDebugSample SampleSimpleDdgiDebug(
    SimpleDdgiParams p,
    vec3 worldPos,
    vec3 normal,
    vec3 viewDir)
{
    uint selectedVolumeIndex;
    SimpleDdgiVolume volume;
    float edgeWeight;
    SelectSimpleDdgiVolume(p, worldPos, selectedVolumeIndex, volume, edgeWeight);
    bool biasedOutsideSelectionDomain;
    vec3 interpolationPosition = SimpleDdgiResolveInterpolationPosition(
        volume,
        worldPos,
        normal,
        viewDir,
        p,
        biasedOutsideSelectionDomain);
    vec3 grid = (interpolationPosition - volume.origin) / volume.spacing;
    ivec3 nearest = ivec3(round(grid));
    nearest = clamp(nearest, ivec3(0), ivec3(volume.gridCount) - ivec3(1));
    uint probeIndex = SimpleDdgiProbeIndex(uvec3(nearest), volume);
    vec3 logicalProbePos = volume.origin + vec3(nearest) * volume.spacing;
    SimpleDdgiProbeState state = ReadSimpleDdgiProbeState(uint(SIMPLE_DDGI_PROBE_STATE_BUFFER_INDEX), probeIndex);
    vec3 probePos = logicalProbePos + state.relocation;
    vec3 toSurface = interpolationPosition - probePos;
    float distanceToProbe = length(toSurface);
    vec3 probeToSurface = distanceToProbe > 0.00001 ? toSurface / distanceToProbe : normalize(normal);
    vec4 moments = SampleSimpleDdgiAtlasBilinear(uint(SIMPLE_DDGI_VISIBILITY_ATLAS_BUFFER_INDEX), probeIndex, probeToSurface, p.visibilityTexels, p);
    float mean = max(moments.x, 0.0);
    float variance = max(moments.y - mean * mean, 0.0);

    SimpleDdgiDebugSample result;
    result.probeIndex = probeIndex;
    result.volumeIndex = selectedVolumeIndex;
    result.logicalProbePosition = logicalProbePos;
    result.relocatedProbePosition = probePos;
    float visibilityBias = clamp(0.03 * p.selfShadowBiasScale * volume.spacing, 0.002, volume.spacing * 0.10);
    result.visibility = SimpleDdgiChebyshev(
        moments.x,
        moments.y,
        max(distanceToProbe - visibilityBias, 0.0),
        volume.spacing);
    result.visibilityMaxRayDistance = max(volume.spacing * float(max(max(volume.gridCount.x, volume.gridCount.y), volume.gridCount.z)), volume.spacing);
    result.visibilityConfidence = mean > 0.0001
        ? clamp(1.0 - sqrt(variance) / max(result.visibilityMaxRayDistance, 0.0001), 0.0, 1.0)
        : 0.0;
    result.visibilityMomentMean = mean;
    result.visibilityMomentVariance = variance;
    result.visibilityProbeDistance = distanceToProbe;
    return result;
}

SimpleDdgiDebugSample SampleSimpleDdgiDebug(vec3 worldPos, vec3 normal, vec3 viewDir)
{
    SimpleDdgiParams p = ReadSimpleDdgiParams(uint(SIMPLE_DDGI_PARAMS_BUFFER_INDEX));
    return SampleSimpleDdgiDebug(p, worldPos, normal, viewDir);
}

vec3 SampleSimpleDdgiIrradiance(vec3 worldPos, vec3 normal, vec3 viewDir)
{
    SimpleDdgiParams p = ReadSimpleDdgiParams(uint(SIMPLE_DDGI_PARAMS_BUFFER_INDEX));
    return SampleSimpleDdgiUnifiedIrradiance(p, worldPos, normal, viewDir, true) * p.indirectIntensity;
}

// Lightweight consumers such as foliage need the probe-field contribution
// without the environment complement that the full forward path composes on
// its own. Keep their estimate and ownership on the Simple DDGI representation.
vec4 SampleSimpleDdgiIrradianceCoverage(vec3 worldPos, vec3 normal, vec3 viewDir)
{
    SimpleDdgiParams p = ReadSimpleDdgiParams(uint(SIMPLE_DDGI_PARAMS_BUFFER_INDEX));
    SimpleDdgiGatherResult gather = SampleSimpleDdgiGather(p, worldPos, normal, viewDir);
    float ownership = SimpleDdgiRadiometricOwnership(gather) * SimpleDdgiLeakAttenuation(gather, p);
    return vec4(
        clamp(gather.irradiance * p.indirectIntensity, vec3(0.0), vec3(64.0)),
        clamp(ownership, 0.0, 1.0));
}

#endif
