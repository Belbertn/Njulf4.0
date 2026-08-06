#ifndef NJULF_DDGI_SIMPLE_SHARED_GLSL
#define NJULF_DDGI_SIMPLE_SHARED_GLSL

// Producer kernels can be specialized at build time so the driver never has
// to lower mutually exclusive legacy and packed storage programs together.
#define SIMPLE_DDGI_STORAGE_SHADER_MODE_DYNAMIC 0
#define SIMPLE_DDGI_STORAGE_SHADER_MODE_LEGACY 1
#define SIMPLE_DDGI_STORAGE_SHADER_MODE_PACKED 2
#ifndef SIMPLE_DDGI_STORAGE_SHADER_MODE
#define SIMPLE_DDGI_STORAGE_SHADER_MODE SIMPLE_DDGI_STORAGE_SHADER_MODE_DYNAMIC
#endif
#ifndef SIMPLE_DDGI_DIRECTION_VALIDATION
#define SIMPLE_DDGI_DIRECTION_VALIDATION 0
#endif

#include "farfield_clipmap.glsl"
#include "ddgi_simple_storage_abi.glsl"
#include "ddgi_simple_receiver_abi.glsl"

// Only the forward fragment path has a meaningful screen-tile coordinate.
// Compute, fog, and particle stages retain the canonical bounded table walk.
// The optional sampled-image mirror is graphics-queue owned. Compute transport
// must read the canonical SSBO field so it never consumes a stale mirror or
// requires an image queue-ownership transfer in the DDGI transaction.
#ifndef SIMPLE_DDGI_DISABLE_SAMPLED_ATLAS
#define SIMPLE_DDGI_DISABLE_SAMPLED_ATLAS 0
#endif

// Producer/solver kernels need the authoritative 32-byte state because they
// can observe transaction-private lifecycle metadata. Receiver shaders leave
// this at zero and consume only the compact 16-byte publication projection.
#ifndef SIMPLE_DDGI_GATHER_USES_COMPUTE_STATE
#define SIMPLE_DDGI_GATHER_USES_COMPUTE_STATE 0
#endif

// Receiver entry points override this with a representative invocation for
// their raster quad, compute tile, particle, or mesh workgroup. Keeping the
// default true preserves correctness for validation shaders while production
// receivers avoid issuing a redundant atomic for every lane that observes the
// same nonresident page.
#ifndef SIMPLE_DDGI_RECEIVER_DEMAND_SAMPLE
#define SIMPLE_DDGI_RECEIVER_DEMAND_SAMPLE true
#endif

// Receiver variants opt in so sparse resident pages remain relevant even when
// they are absent from the proactive depth predictor.
#ifndef SIMPLE_DDGI_RECEIVER_TOUCHES_RESIDENT
#define SIMPLE_DDGI_RECEIVER_TOUCHES_RESIDENT 0
#endif

// Opaque receivers execute before the current reconciliation and use offset
// zero. Transparent, particle, and fog receivers execute after feedback and
// target the next epoch with offset one.
#ifndef SIMPLE_DDGI_RECEIVER_DEMAND_FRAME_OFFSET
#define SIMPLE_DDGI_RECEIVER_DEMAND_FRAME_OFFSET 1u
#endif

#ifndef SIMPLE_DDGI_OPAQUE_GATHER_ORACLE
#define SIMPLE_DDGI_OPAQUE_GATHER_ORACLE 0
#endif

// Release/ShippingPerformance pass zero from the shader project. This is a
// compile-time boundary, not a runtime feature flag: production SPIR-V contains
// no reachable DDGI investigation atomic or diagnostic sampling branch.
#ifndef NJULF_DDGI_DETAILED_COUNTERS
#define NJULF_DDGI_DETAILED_COUNTERS 0
#endif

// The source cache is update-side authority, not part of the compact receiver
// contract. Only the forward diagnostic artifact opts into the additional
// compute-state/source-cache dependency; every other receiver stays compact in
// detailed builds as well as production builds.
#ifndef NJULF_SIMPLE_DDGI_SOURCE_CACHE_DIAGNOSTIC
#define NJULF_SIMPLE_DDGI_SOURCE_CACHE_DIAGNOSTIC 0
#endif

#ifndef NJULF_SIMPLE_DDGI_GATHER_ATTRIBUTION
#if NJULF_DDGI_DETAILED_COUNTERS
#define NJULF_SIMPLE_DDGI_GATHER_ATTRIBUTION 1
#elif defined(NJULF_MATERIAL_TRANSPORT_PROVENANCE_OUTPUT) && NJULF_MATERIAL_TRANSPORT_PROVENANCE_OUTPUT
#define NJULF_SIMPLE_DDGI_GATHER_ATTRIBUTION 1
#else
#define NJULF_SIMPLE_DDGI_GATHER_ATTRIBUTION 0
#endif
#endif

const float SIMPLE_DDGI_PI = 3.14159265359;
// Producer shaders include this file without the scheduler-stage shared
// header, so mirror both queue and private transaction strides here.
const uint SIMPLE_DDGI_SCHEDULER_UPDATE_WORDS = 12u;
// Relocate/classify overwrites proposal words 0..6 while immutable sparse
// physical identity remains in words 7..8 for commit validation.
const uint SIMPLE_DDGI_SCHEDULER_UPDATE_RECORD_WORDS = 10u;
const uint SIMPLE_DDGI_SCHEDULER_OUTCOME_WORDS = 15u;
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
// Legacy retains the historical frame quaternion. Validate and Packed use the
// persistent five-bit codebook; Validate also shadows reconstructed directions
// against the still-stored octahedral cache payload.
const uint SIMPLE_DDGI_FLAG_DIRECTION_CODEBOOK = 1u << 22;
const uint SIMPLE_DDGI_FLAG_DIRECTION_VALIDATE = 1u << 30;
// Bit 23 is reserved for the CPU-recorded coarse-to-fine solve phase. The
// selected volume index is carried in the existing primary-light push field
// for transport/blend/intermediate-publish shaders, which do not otherwise use
// that field.
const uint SIMPLE_DDGI_SOLVE_VOLUME_FILTER = 1u << 23;
// These bits live only in compute-pass push constants, not in the persistent
// params header.  They describe one ordered red/black cached solve phase:
// filter by logical color, identify the first color for first-sweep-only
// lifecycle work, and delay scheduler completion until the final color.
const uint SIMPLE_DDGI_SOLVE_COLOR_FILTER = 1u << 24;
const uint SIMPLE_DDGI_SOLVE_FIRST_COLOR = 1u << 25;
const uint SIMPLE_DDGI_SOLVE_FINAL_COLOR = 1u << 26;
const uint SIMPLE_DDGI_SOLVE_COLOR_SHIFT = 27u;
const uint SIMPLE_DDGI_SOLVE_COLOR_MASK = 1u << SIMPLE_DDGI_SOLVE_COLOR_SHIFT;
const uint SIMPLE_DDGI_SOLVE_SWEEP_INDEX_SHIFT = 28u;
// A normalized [0, 1] second-volume early-out threshold is packed into the
// otherwise-unused high flag bits. This preserves the fixed params header ABI.
const uint SIMPLE_DDGI_SECOND_VOLUME_OWNERSHIP_THRESHOLD_SHIFT = 12u;
const uint SIMPLE_DDGI_SECOND_VOLUME_OWNERSHIP_THRESHOLD_MASK = 0xffu << SIMPLE_DDGI_SECOND_VOLUME_OWNERSHIP_THRESHOLD_SHIFT;
const uint SIMPLE_DDGI_IRRADIANCE_TEXELS = 8u;
const uint SIMPLE_DDGI_VISIBILITY_TEXELS = 16u;
const uint SIMPLE_DDGI_MAX_RAYS_PER_PROBE = 256u;
const uint SIMPLE_DDGI_LEGACY_RAY_RESULT_STRIDE_WORDS = 8u;
const uint SIMPLE_DDGI_RAY_RESULT_STRIDE_WORDS = 5u;
const uint SIMPLE_DDGI_TRANSPORT_RAY_CACHE_ABI_VERSION = 5u;
const uint SIMPLE_DDGI_HEADER_WORDS = 60u;
const uint SIMPLE_DDGI_FLAGS_WORD = 14u;
const uint SIMPLE_DDGI_VOLUME_STRIDE_WORDS = 28u;
const uint SIMPLE_DDGI_VOLUME_PAGING_STRIDE_WORDS = 8u;
const uint SIMPLE_DDGI_MAX_VOLUME_COUNT = 16u;
const uint SIMPLE_DDGI_VOLUME_PAGING_BASE_WORD =
    SIMPLE_DDGI_HEADER_WORDS +
    SIMPLE_DDGI_MAX_VOLUME_COUNT * SIMPLE_DDGI_VOLUME_STRIDE_WORDS;
const uint SIMPLE_DDGI_RESIDENCY_MODE_DENSE = 0u;
const uint SIMPLE_DDGI_RESIDENCY_MODE_SHADOW = 1u;
const uint SIMPLE_DDGI_RESIDENCY_MODE_SPARSE_NEAR_RING = 2u;
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
const uint SIMPLE_DDGI_GATHER_REJECTION_REASON_COUNT = 10u;
const uint SIMPLE_DDGI_GATHER_REJECT_FRESH = 1u << 0;
const uint SIMPLE_DDGI_GATHER_REJECT_SCROLL_EXPOSED = 1u << 1;
const uint SIMPLE_DDGI_GATHER_REJECT_RELOCATION_PENDING = 1u << 2;
const uint SIMPLE_DDGI_GATHER_REJECT_INACTIVE_FLAG = 1u << 3;
const uint SIMPLE_DDGI_GATHER_REJECT_INACTIVE_CLASSIFICATION = 1u << 4;
const uint SIMPLE_DDGI_GATHER_REJECT_ZERO_ACTIVE_WEIGHT = 1u << 5;
const uint SIMPLE_DDGI_GATHER_REJECT_INVALID_IRRADIANCE = 1u << 6;
const uint SIMPLE_DDGI_GATHER_REJECT_INVALID_VISIBILITY = 1u << 7;
const uint SIMPLE_DDGI_GATHER_REJECT_OUTSIDE_DOMAIN = 1u << 8;
const uint SIMPLE_DDGI_GATHER_REJECT_NON_RESIDENT = 1u << 9;
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
const uint SIMPLE_DDGI_PROBE_UPDATE_STRIDE_WORDS = 12u;
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
const uint SIMPLE_DDGI_PROBE_FLAG_VISIBILITY_VALID = 1u << 5;
const uint SIMPLE_DDGI_PROBE_FLAG_NON_RESIDENT = 1u << 6;
const uint SIMPLE_DDGI_UPDATE_MATERIAL_TEXTURE_CASCADE_SHIFT = 3u;
const uint SIMPLE_DDGI_UPDATE_MATERIAL_TEXTURE_CASCADE_MASK = 0x7u << SIMPLE_DDGI_UPDATE_MATERIAL_TEXTURE_CASCADE_SHIFT;
const uint SIMPLE_DDGI_UPDATE_MAX_SHADED_LIGHTS_SHIFT = 6u;
const uint SIMPLE_DDGI_UPDATE_MAX_SHADED_LIGHTS_MASK = 0x3fu << SIMPLE_DDGI_UPDATE_MAX_SHADED_LIGHTS_SHIFT;
const uint SIMPLE_DDGI_UPDATE_MAINTENANCE = 1u << 12;
const uint SIMPLE_DDGI_UPDATE_SOURCE_REFRESH = 1u << 13;
const uint SIMPLE_DDGI_UPDATE_INVALIDATE = 1u << 14;
// The remaining state-flag bits carry a non-zero physical-slot generation.  An
// update recorded for an old toroidal mapping must never mutate the slot after it
// has been re-exposed for a new world cell.
const uint SIMPLE_DDGI_PROBE_FLAG_GENERATION_SHIFT = 8u;
const uint SIMPLE_DDGI_PROBE_FLAG_GENERATION_MASK = 0xffffff00u;
const uint SIMPLE_DDGI_SCHEDULER_INVALID_INDEX = 0xffffffffu;
// Ray-bucket commands and metadata are separate arena regions. The command
// offset is used only by the host's vkCmdDispatchIndirect call; shaders read
// queue metadata through the dedicated metadata offset.
const uint SIMPLE_DDGI_SCHEDULER_RAY_BUCKET_INDIRECT_WORDS = 4u;
const uint SIMPLE_DDGI_SCHEDULER_RAY_BUCKET_METADATA_WORDS = 4u;
const uint SIMPLE_DDGI_SCHEDULER_COUNTER_ACCEPTED_WORD = 2u;
const uint SIMPLE_DDGI_SCHEDULER_COMPLETE_CANONICAL_PUBLISH = 1u << 5u;
const uint SIMPLE_DDGI_SCHEDULER_COMPLETE_SAMPLED_PUBLISH = 1u << 6u;
const uint SIMPLE_DDGI_SCHEDULER_FAILURE_PUBLICATION_GUARD = 1u << 3u;

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

// Publication is still a producer stage, but it is not allowed to start from
// a partial transaction. The scheduler commit validates the same outcome
// record again after publication; this first guard prevents a stale generation
// or failed producer from copying private data into a receiver-visible atlas.
bool SimpleDdgiSchedulerOutcomeAllowsPublication(
    uint arenaIndex,
    uint outcomesOffsetWords,
    uint outcomeIndex,
    bool sampledPublication,
    uint frameOffsetWords)
{
    if (!SimpleDdgiGpuSchedulerActive(arenaIndex))
        return true;
    uint base = outcomesOffsetWords + outcomeIndex * SIMPLE_DDGI_SCHEDULER_OUTCOME_WORDS;
    uint required = SimpleDdgiSchedulerRead(arenaIndex, base + 7u);
    uint completed = SimpleDdgiSchedulerRead(arenaIndex, base + 8u);
    uint failure = SimpleDdgiSchedulerRead(arenaIndex, base + 9u);
    if (failure != 0u)
        return false;

    // Publication is a producer boundary, so it must reject a delayed queue
    // record even when its completion bits happen to be present. Emit records
    // the five transaction generations in words 0..4; compare them with the
    // current frame header before any receiver-visible atlas write.
    if (SimpleDdgiSchedulerRead(arenaIndex, base + 0u) !=
            SimpleDdgiSchedulerRead(arenaIndex, frameOffsetWords + 16u) ||
        SimpleDdgiSchedulerRead(arenaIndex, base + 1u) !=
            SimpleDdgiSchedulerRead(arenaIndex, frameOffsetWords + 15u) ||
        SimpleDdgiSchedulerRead(arenaIndex, base + 2u) !=
            SimpleDdgiSchedulerRead(arenaIndex, frameOffsetWords + 14u) ||
        SimpleDdgiSchedulerRead(arenaIndex, base + 3u) !=
            SimpleDdgiSchedulerRead(arenaIndex, frameOffsetWords + 17u) ||
        SimpleDdgiSchedulerRead(arenaIndex, base + 4u) !=
            SimpleDdgiSchedulerRead(arenaIndex, frameOffsetWords + 18u))
        return false;

    uint prerequisite = required & ~(
        SIMPLE_DDGI_SCHEDULER_COMPLETE_CANONICAL_PUBLISH |
        SIMPLE_DDGI_SCHEDULER_COMPLETE_SAMPLED_PUBLISH);
    if (sampledPublication)
        prerequisite |= SIMPLE_DDGI_SCHEDULER_COMPLETE_CANONICAL_PUBLISH;
    return (completed & prerequisite) == prerequisite;
}

void SimpleDdgiResolveSchedulerRayBucket(
    uint arenaIndex,
    uint bucketMetadataOffsetWords,
    uint bucketIndex,
    out uint queueOffset,
    out uint probeCount,
    out uint raysPerProbe)
{
    uint base = bucketMetadataOffsetWords +
        bucketIndex * SIMPLE_DDGI_SCHEDULER_RAY_BUCKET_METADATA_WORDS;
    queueOffset = SimpleDdgiSchedulerRead(arenaIndex, base + 0u);
    probeCount = SimpleDdgiSchedulerRead(arenaIndex, base + 1u);
    raysPerProbe = max(1u, SimpleDdgiSchedulerRead(arenaIndex, base + 2u));
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

const uint SIMPLE_DDGI_RAY_METADATA_HIT_KIND_SHIFT = 16u;
const uint SIMPLE_DDGI_RAY_METADATA_HIT_KIND_MASK = 0x7u <<
    SIMPLE_DDGI_RAY_METADATA_HIT_KIND_SHIFT;
const uint SIMPLE_DDGI_RAY_METADATA_EPOCH_SHIFT = 19u;
const uint SIMPLE_DDGI_RAY_METADATA_EPOCH_MASK = 0x1fu <<
    SIMPLE_DDGI_RAY_METADATA_EPOCH_SHIFT;
const uint SIMPLE_DDGI_RAY_METADATA_VALID_BIT = 1u << 24u;
const uint SIMPLE_DDGI_RAY_METADATA_RESERVED_MASK = 0xfe000000u;

uint PackSimpleDdgiRayMetadata(
    float visibilityDistance,
    float hitKind,
    uint sourceEpoch)
{
    bool valid = !isnan(visibilityDistance) && !isinf(visibilityDistance) &&
        visibilityDistance >= 0.0 &&
        !isnan(hitKind) && !isinf(hitKind) &&
        hitKind >= SIMPLE_DDGI_RAY_HIT_KIND_MISS &&
        hitKind <= SIMPLE_DDGI_RAY_HIT_KIND_FAR_FIELD_BACK_FACE &&
        hitKind == round(hitKind) &&
        sourceEpoch != 0u;
    if (!valid)
        return 0u;
    uint distanceHalf = packHalf2x16(vec2(
        clamp(visibilityDistance, 0.0, 65504.0),
        0.0)) & 0xffffu;
    uint exactHitKind = uint(clamp(
        round(hitKind),
        SIMPLE_DDGI_RAY_HIT_KIND_MISS,
        SIMPLE_DDGI_RAY_HIT_KIND_FAR_FIELD_BACK_FACE));
    return distanceHalf |
        (exactHitKind << SIMPLE_DDGI_RAY_METADATA_HIT_KIND_SHIFT) |
        ((sourceEpoch & 0x1fu) << SIMPLE_DDGI_RAY_METADATA_EPOCH_SHIFT) |
        SIMPLE_DDGI_RAY_METADATA_VALID_BIT;
}

bool UnpackSimpleDdgiRayMetadata(
    uint metadata,
    out vec2 visibilityHit,
    out uint directionEpoch)
{
    visibilityHit = vec2(
        unpackHalf2x16(metadata & 0xffffu).x,
        float((metadata & SIMPLE_DDGI_RAY_METADATA_HIT_KIND_MASK) >>
            SIMPLE_DDGI_RAY_METADATA_HIT_KIND_SHIFT));
    directionEpoch = (metadata & SIMPLE_DDGI_RAY_METADATA_EPOCH_MASK) >>
        SIMPLE_DDGI_RAY_METADATA_EPOCH_SHIFT;
    return (metadata & SIMPLE_DDGI_RAY_METADATA_VALID_BIT) != 0u &&
        (metadata & SIMPLE_DDGI_RAY_METADATA_RESERVED_MASK) == 0u &&
        visibilityHit.y <= SIMPLE_DDGI_RAY_HIT_KIND_FAR_FIELD_BACK_FACE;
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
    uint sampledAtlasProbeCapacity;
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
    float transportTailRelativeTolerance;
    uint transportAcceleratedSweepCount;
    uint residencyArenaBufferIndex;
    uint virtualPageCount;
    uint physicalProbeCapacity;
    uint residencyResourceGeneration;
    uint residencyMode;
    uint densePhysicalProbeCount;
    uint sparsePhysicalPageCapacity;
    uint residencyFlags;
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

uint SimpleDdgiStorageDiagnosticFrame(SimpleDdgiParams params)
{
    return params.frameIndex == 0xffffffffu
        ? 0u
        : params.frameIndex % uint(FRAMES_IN_FLIGHT);
}

// Detailed qualification must remain representative without putting a global
// atomic on every ray and every receiver corner. Hash stable storage identities
// into a deterministic 1/64 sample; exceptional counters remain exact below.
bool SimpleDdgiStorageDiagnosticSample(
    uint first,
    uint second,
    uint third)
{
    // Odd coefficients are permutations modulo 64. Consequently every run of
    // 64 consecutive ray/texel identities contributes exactly one sample,
    // independent of the other stable keys. Besides providing a stronger
    // coverage guarantee than a probabilistic avalanche hash, this bounded
    // form avoids overflow-heavy integer lowering in native driver compilers.
    uint value = first * 29u + second * 17u + third * 31u;
    return (value & 63u) == 0u;
}

void RecordSimpleDdgiCachePackAttempt(
    SimpleDdgiParams params,
    uint volumeIndex,
    uint physicalProbeIndex,
    uint directionRayIndex,
    bool payloadFinite,
    bool radianceSaturated,
    float maximumRadianceError,
    float distanceError)
{
#if NJULF_DDGI_DETAILED_COUNTERS
    if (!SimpleDdgiDetailedDiagnosticsEnabled(params))
        return;
    uint diagnosticFrame = SimpleDdgiStorageDiagnosticFrame(params);
    bool sampled = SimpleDdgiStorageDiagnosticSample(
        volumeIndex,
        physicalProbeIndex,
        directionRayIndex);
    if (sampled)
    {
        AddSimpleDdgiStorageValidationDiagnostic(
            diagnosticFrame,
            SIMPLE_DDGI_CACHE_PACK_ATTEMPT_COUNTER,
            1u);
    }
    if (!payloadFinite)
    {
        AddSimpleDdgiStorageValidationDiagnostic(
            diagnosticFrame,
            SIMPLE_DDGI_CACHE_PACK_NONFINITE_COUNTER,
            1u);
        return;
    }
    if (radianceSaturated)
    {
        AddSimpleDdgiStorageValidationDiagnostic(
            diagnosticFrame,
            SIMPLE_DDGI_CACHE_PACK_RADIANCE_SATURATION_COUNTER,
            1u);
    }
    if (!sampled)
        return;
    MaxSimpleDdgiStorageValidationDiagnostic(
        diagnosticFrame,
        SIMPLE_DDGI_CACHE_PACK_MAX_RADIANCE_ERROR_COUNTER,
        uint(round(clamp(maximumRadianceError, 0.0, 4294.0) * 1000000.0)));
    MaxSimpleDdgiStorageValidationDiagnostic(
        diagnosticFrame,
        SIMPLE_DDGI_CACHE_PACK_MAX_DISTANCE_ERROR_COUNTER,
        uint(round(clamp(distanceError, 0.0, 4294.0) * 1000000.0)));
#endif
}

void RecordSimpleDdgiDirectionEpochMismatch(SimpleDdgiParams params)
{
#if NJULF_DDGI_DETAILED_COUNTERS
    if (SimpleDdgiDetailedDiagnosticsEnabled(params))
    {
        AddSimpleDdgiStorageValidationDiagnostic(
            SimpleDdgiStorageDiagnosticFrame(params),
            SIMPLE_DDGI_DIRECTION_EPOCH_MISMATCH_COUNTER,
            1u);
    }
#endif
}

void RecordSimpleDdgiInvalidRayMetadata(
    SimpleDdgiParams params,
    bool invalidSourceEpoch,
    bool invalidHitKind)
{
#if NJULF_DDGI_DETAILED_COUNTERS
    if (!SimpleDdgiDetailedDiagnosticsEnabled(params))
        return;
    uint diagnosticFrame = SimpleDdgiStorageDiagnosticFrame(params);
    if (invalidSourceEpoch)
    {
        AddSimpleDdgiStorageValidationDiagnostic(
            diagnosticFrame,
            SIMPLE_DDGI_INVALID_SOURCE_EPOCH_COUNTER,
            1u);
    }
    if (invalidHitKind)
    {
        AddSimpleDdgiStorageValidationDiagnostic(
            diagnosticFrame,
            SIMPLE_DDGI_INVALID_HIT_KIND_COUNTER,
            1u);
    }
#endif
}

void RecordSimpleDdgiDirectionComparison(
    SimpleDdgiParams params,
    uint diagnosticFrameIndex,
    uint probeIndex,
    uint directionRayIndex,
    vec3 storedDirection,
    vec3 reconstructedDirection)
{
#if NJULF_DDGI_DETAILED_COUNTERS
    if (!SimpleDdgiDetailedDiagnosticsEnabled(params))
        return;
    if (!SimpleDdgiStorageDiagnosticSample(
            probeIndex,
            directionRayIndex,
            params.frameIndex))
    {
        return;
    }
    float cosine = clamp(dot(
        normalize(storedDirection),
        normalize(reconstructedDirection)), -1.0, 1.0);
    float angularError = acos(cosine);
    uint bucket = angularError <= 0.000001 ? 0u
        : angularError <= 0.000005 ? 1u
        : angularError <= 0.000010 ? 2u
        : angularError <= 0.000025 ? 3u
        : angularError <= 0.000050 ? 4u
        : angularError <= 0.000100 ? 5u
        : angularError <= 0.000250 ? 6u
        : 7u;
    uint diagnosticFrame = diagnosticFrameIndex % uint(FRAMES_IN_FLIGHT);
    AddSimpleDdgiStorageValidationDiagnostic(
        diagnosticFrame,
        SIMPLE_DDGI_DIRECTION_COMPARE_SAMPLE_COUNTER,
        1u);
    AddSimpleDdgiStorageValidationDiagnostic(
        diagnosticFrame,
        SIMPLE_DDGI_DIRECTION_ANGULAR_HISTOGRAM_BASE + bucket,
        1u);
    MaxSimpleDdgiStorageValidationDiagnostic(
        diagnosticFrame,
        SIMPLE_DDGI_DIRECTION_MAX_ANGULAR_ERROR_COUNTER,
        uint(round(clamp(angularError, 0.0, 4294.0) * 1000000.0)));
#endif
}

void RecordSimpleDdgiMirrorSamplingPath(
    SimpleDdgiParams params,
    uint sampleKey,
    bool interior,
    bool imageHit,
    bool mirrorPayloadDeclared)
{
#if NJULF_DDGI_DETAILED_COUNTERS
    if (!SimpleDdgiDetailedDiagnosticsEnabled(params) ||
        params.sampledAtlasEnabled == 0u)
        return;
    uint diagnosticFrame = SimpleDdgiStorageDiagnosticFrame(params);
    bool sampled = SimpleDdgiStorageDiagnosticSample(
        sampleKey,
        params.frameIndex,
        mirrorPayloadDeclared ? 1u : 0u);
    if (!interior)
    {
        if (sampled)
        {
            AddSimpleDdgiStorageValidationDiagnostic(
                diagnosticFrame,
                SIMPLE_DDGI_MIRROR_SEAM_FALLBACK_COUNTER,
                1u);
        }
        return;
    }
    if (mirrorPayloadDeclared && !imageHit)
    {
        // Invalid compact mappings are exceptional and must never disappear in
        // sampling noise; count them exactly for the zero-error gate.
        AddSimpleDdgiStorageValidationDiagnostic(
            diagnosticFrame,
            SIMPLE_DDGI_MIRROR_INVALID_MAP_FALLBACK_COUNTER,
            1u);
        return;
    }
    if (!sampled)
        return;
    AddSimpleDdgiStorageValidationDiagnostic(
        diagnosticFrame,
        SIMPLE_DDGI_MIRROR_INTERIOR_OPPORTUNITY_COUNTER,
        1u);
    AddSimpleDdgiStorageValidationDiagnostic(
        diagnosticFrame,
        imageHit
            ? SIMPLE_DDGI_MIRROR_IMAGE_HIT_COUNTER
            : SIMPLE_DDGI_MIRROR_UNMIRRORED_FALLBACK_COUNTER,
        1u);
#endif
}

bool SimpleDdgiMirrorPayloadOrMappingDeclared(
    uint compactFirstLayerPlusOne,
    uint layoutFlags,
    uint payloadBit)
{
    // Either half of the declaration is enough to make a failed image path an
    // invalid-map event. This catches both a payload bit without a layer base
    // and a layer base whose typed payload bit was lost or corrupted.
    return compactFirstLayerPlusOne != 0u ||
        (layoutFlags & payloadBit) != 0u;
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
const uint SIMPLE_DDGI_VOLUME_ENERGY_COUNTER_BASE = 324u;
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
    uint requiredSourceRayCount;
    uint sourceOrdinal;
    uvec3 physicalOffset;
    uint cacheBaseWord;
    uint cacheStrideWords;
    uint compactMirrorFirstLayerPlusOne;
    uint cacheLayoutFlags;
};

// Receiver/update code keeps the historical CACHE names, but every ABI value
// aliases the single authoritative storage contract included above.
const uint SIMPLE_DDGI_CACHE_FORMAT_MASK = SIMPLE_DDGI_STORAGE_FORMAT_MASK;
const uint SIMPLE_DDGI_CACHE_FORMAT_LEGACY_36 =
    SIMPLE_DDGI_STORAGE_FORMAT_LEGACY_36;
const uint SIMPLE_DDGI_CACHE_FORMAT_COMPACT_28 =
    SIMPLE_DDGI_STORAGE_FORMAT_COMPACT_28;
const uint SIMPLE_DDGI_CACHE_FORMAT_COMPACT_24 =
    SIMPLE_DDGI_STORAGE_FORMAT_COMPACT_24;
const uint SIMPLE_DDGI_CACHE_FORMAT_INVALID =
    SIMPLE_DDGI_STORAGE_FORMAT_INVALID;
const uint SIMPLE_DDGI_CACHE_IRRADIANCE_MIRROR_BIT = 1u << 2u;
const uint SIMPLE_DDGI_CACHE_VISIBILITY_MIRROR_BIT = 1u << 3u;
const uint SIMPLE_DDGI_CACHE_ABI_SHIFT = SIMPLE_DDGI_STORAGE_ABI_SHIFT;
const uint SIMPLE_DDGI_CACHE_ABI_MASK = SIMPLE_DDGI_STORAGE_ABI_MASK;
const uint SIMPLE_DDGI_CACHE_CODEBOOK_SHIFT =
    SIMPLE_DDGI_STORAGE_CODEBOOK_SHIFT;
const uint SIMPLE_DDGI_CACHE_CODEBOOK_MASK =
    SIMPLE_DDGI_STORAGE_CODEBOOK_MASK;
const uint SIMPLE_DDGI_CACHE_RESERVED_MASK =
    SIMPLE_DDGI_STORAGE_RESERVED_MASK;
const uint SIMPLE_DDGI_DIRECTION_CODEBOOK_VERSION =
    SIMPLE_DDGI_STORAGE_CODEBOOK_VERSION;

struct SimpleDdgiVolumePaging
{
    uint virtualFirstProbe;
    uint pageTableFirst;
    uint densePhysicalFirstProbe;
    uint residencyMode;
    uvec3 pageGrid;
    uint sparsePoolFirstProbe;
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
    uint residencyTableFlags;
    uint residencyHistoryFlags;
    uint residencyDemandMask;
    uint physicalPageIndex;
    uint pageMappingGeneration;
    uint pageAgeFrames;
    float pageAgeNormalized;
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
    uint sourceLightingGeneration;
    uint outcomeIndex;
    uint sourceEpoch;
    uint physicalProbeIndex;
    uint pageMappingGeneration;
    uint residencyResourceGeneration;
    uint cacheProbeBaseWordPlusOne;
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
    update.sourceLightingGeneration = ReadStorageWordUniform(bufferIndex, baseWord + 5u);
    update.outcomeIndex = ReadStorageWordUniform(bufferIndex, baseWord + 6u);
    update.sourceEpoch = ReadStorageWordUniform(bufferIndex, baseWord + 7u);
    update.physicalProbeIndex = ReadStorageWordUniform(bufferIndex, baseWord + 8u);
    update.pageMappingGeneration = ReadStorageWordUniform(bufferIndex, baseWord + 9u);
    update.residencyResourceGeneration = ReadStorageWordUniform(bufferIndex, baseWord + 10u);
    update.cacheProbeBaseWordPlusOne = ReadStorageWordUniform(bufferIndex, baseWord + 11u);
    return update;
}

// Every ray producer performs its output write, a buffer memory barrier, and
// then this atomic completion protocol. Only the invocation that observes the
// final expected ray count sets the producer bit.
void SimpleDdgiSchedulerRayComplete(
    uint arenaIndex,
    uint outcomesOffsetWords,
    uint outcomeIndex,
    uint expectedInvocationCount,
    uint completionBit)
{
    if (!SimpleDdgiGpuSchedulerActive(arenaIndex))
        return;
    memoryBarrierBuffer();
    uint base = outcomesOffsetWords + outcomeIndex * SIMPLE_DDGI_SCHEDULER_OUTCOME_WORDS;
    uint countWord = completionBit == (1u << 0u) ? 12u : 13u;
    uint previous = atomicAdd(
        BindlessStorageBuffers[nonuniformEXT(arenaIndex)].Words[base + countWord], 1u);
    if (previous + 1u >= max(expectedInvocationCount, 1u))
    {
        atomicOr(
            BindlessStorageBuffers[nonuniformEXT(arenaIndex)].Words[base + 8u],
            completionBit);
    }
}

void SimpleDdgiSchedulerFailOutcome(
    uint arenaIndex,
    uint outcomesOffsetWords,
    uint outcomeIndex,
    uint failureReason)
{
    if (!SimpleDdgiGpuSchedulerActive(arenaIndex))
        return;
    atomicOr(
        BindlessStorageBuffers[nonuniformEXT(arenaIndex)].Words[
            outcomesOffsetWords + outcomeIndex * SIMPLE_DDGI_SCHEDULER_OUTCOME_WORDS + 9u],
        failureReason);
}

void SimpleDdgiSchedulerCompleteSingle(
    uint arenaIndex,
    uint outcomesOffsetWords,
    uint outcomeIndex,
    uint completionBit)
{
    if (!SimpleDdgiGpuSchedulerActive(arenaIndex))
        return;
    memoryBarrierBuffer();
    atomicOr(
        BindlessStorageBuffers[nonuniformEXT(arenaIndex)].Words[
            outcomesOffsetWords + outcomeIndex * SIMPLE_DDGI_SCHEDULER_OUTCOME_WORDS + 8u],
        completionBit);
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
    vec4 residencyAndCounts = ReadStorageAlignedVec4Uniform(bufferIndex, 52u);
    vec4 residencyControls = ReadStorageAlignedVec4Uniform(bufferIndex, 56u);
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
    p.sampledAtlasProbeCapacity = uint(max(biasLimits.w, 0.0));
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
    p.transportTailRelativeTolerance = clamp(transportControls.z, 0.0, 1.0);
    p.transportAcceleratedSweepCount = clamp(uint(max(transportControls.w, 1.0)), 1u, 4u);
    p.residencyArenaBufferIndex = floatBitsToUint(residencyAndCounts.x);
    p.virtualPageCount = floatBitsToUint(residencyAndCounts.y);
    p.physicalProbeCapacity = floatBitsToUint(residencyAndCounts.z);
    p.residencyResourceGeneration = floatBitsToUint(residencyAndCounts.w);
    p.residencyMode = floatBitsToUint(residencyControls.x);
    p.densePhysicalProbeCount = floatBitsToUint(residencyControls.y);
    p.sparsePhysicalPageCapacity = floatBitsToUint(residencyControls.z);
    p.residencyFlags = floatBitsToUint(residencyControls.w);
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
    vec4 cacheLayout = ReadStorageAlignedVec4Uniform(bufferIndex, baseWord + 24u);

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
    volume.requiredSourceRayCount = uint(max(updateRange.z, 0.0));
    volume.sourceOrdinal = uint(max(raysAndReserved.x, 0.0));
    volume.physicalOffset = uvec3(max(raysAndReserved.yzw, vec3(0.0)));
    volume.cacheBaseWord = floatBitsToUint(cacheLayout.x);
    volume.cacheStrideWords = floatBitsToUint(cacheLayout.y);
    volume.compactMirrorFirstLayerPlusOne = floatBitsToUint(cacheLayout.z);
    volume.cacheLayoutFlags = floatBitsToUint(cacheLayout.w);
    return volume;
}

SimpleDdgiVolumePaging ReadSimpleDdgiVolumePaging(
    uint bufferIndex,
    uint volumeIndex)
{
    uint baseWord = SIMPLE_DDGI_VOLUME_PAGING_BASE_WORD +
        volumeIndex * SIMPLE_DDGI_VOLUME_PAGING_STRIDE_WORDS;
    uvec4 addressing = ReadStorageAlignedUVec4Uniform(
        bufferIndex,
        baseWord);
    uvec4 pageLayout = ReadStorageAlignedUVec4Uniform(
        bufferIndex,
        baseWord + 4u);
    SimpleDdgiVolumePaging paging;
    paging.virtualFirstProbe = addressing.x;
    paging.pageTableFirst = addressing.y;
    paging.densePhysicalFirstProbe = addressing.z;
    paging.residencyMode = addressing.w;
    paging.pageGrid = pageLayout.xyz;
    paging.sparsePoolFirstProbe = pageLayout.w;
    return paging;
}

#include "ddgi_simple_page_shared.glsl"

uint SimpleDdgiRequiredSourceRayCount(
    SimpleDdgiParams params,
    SimpleDdgiVolume volume)
{
    uint maximum = min(params.raysPerProbe, SIMPLE_DDGI_MAX_RAYS_PER_PROBE);
    return volume.requiredSourceRayCount >= 1u &&
        volume.requiredSourceRayCount <= maximum
        ? volume.requiredSourceRayCount
        : 0u;
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

bool SimpleDdgiUpdateMatchesLiveAddress(
    SimpleDdgiParams params,
    SimpleDdgiProbeUpdate update)
{
    bool denseResidency = params.residencyMode ==
        SIMPLE_DDGI_RESIDENCY_MODE_DENSE;
    if ((denseResidency &&
            update.residencyResourceGeneration != 0u) ||
        (!denseResidency &&
            (update.residencyResourceGeneration == 0u ||
             update.residencyResourceGeneration !=
                params.residencyResourceGeneration)))
    {
        SimpleDdgiRecordResidencyCounter(
            params,
            SIMPLE_DDGI_RESIDENCY_COUNTER_STALE_RESOURCE,
            1u);
        return false;
    }
    if (update.probeIndex >= params.probeCount ||
        update.volumeIndex >= params.volumeCount ||
        update.physicalProbeIndex >= params.physicalProbeCapacity ||
        update.pageMappingGeneration == 0u)
    {
        SimpleDdgiRecordResidencyCounter(
            params,
            SIMPLE_DDGI_RESIDENCY_COUNTER_OUT_OF_RANGE,
            1u);
        return false;
    }
    SimpleDdgiVolume volume = ReadSimpleDdgiVolume(
        uint(SIMPLE_DDGI_PARAMS_BUFFER_INDEX),
        update.volumeIndex);
    uint probeCount = SimpleDdgiVolumeProbeCount(volume);
    if (update.probeIndex < volume.firstProbeIndex ||
        update.probeIndex - volume.firstProbeIndex >= probeCount)
    {
        SimpleDdgiRecordResidencyCounter(
            params,
            SIMPLE_DDGI_RESIDENCY_COUNTER_OUT_OF_RANGE,
            1u);
        return false;
    }
    uint localPhysical = update.probeIndex - volume.firstProbeIndex;
    uvec3 logicalCoord = SimpleDdgiProbeCoord(localPhysical, volume);
    SimpleDdgiVolumePaging paging = ReadSimpleDdgiVolumePaging(
        uint(SIMPLE_DDGI_PARAMS_BUFFER_INDEX),
        update.volumeIndex);
    SimpleDdgiProbeAddress live = ResolveSimpleDdgiProbeAddress(
        params,
        volume,
        paging,
        logicalCoord);
    bool matches = live.resident &&
        live.physicalProbeIndex == update.physicalProbeIndex &&
        live.pageMappingGeneration == update.pageMappingGeneration;
    if (!matches)
    {
        SimpleDdgiRecordResidencyCounter(
            params,
            SIMPLE_DDGI_RESIDENCY_COUNTER_STALE_MAPPING,
            1u);
    }
    return matches;
}

bool SimpleDdgiUpdateMatchesProbeGenerationAndRecord(
    SimpleDdgiParams params,
    SimpleDdgiProbeUpdate update,
    SimpleDdgiProbeState state)
{
    bool matches = SimpleDdgiUpdateMatchesProbeGeneration(update, state);
    if (!matches)
    {
        SimpleDdgiRecordResidencyCounter(
            params,
            SIMPLE_DDGI_RESIDENCY_COUNTER_STALE_VIRTUAL,
            1u);
    }
    return matches;
}

uint SimpleDdgiSolveTargetColor(uint flags)
{
    return (flags & SIMPLE_DDGI_SOLVE_COLOR_MASK) >>
        SIMPLE_DDGI_SOLVE_COLOR_SHIFT;
}

bool SimpleDdgiSolveColorMatches(
    uint flags,
    SimpleDdgiVolume volume,
    uint localProbeIndex)
{
    if ((flags & SIMPLE_DDGI_SOLVE_COLOR_FILTER) == 0u)
        return true;

    uvec3 logical = SimpleDdgiProbeCoord(localProbeIndex, volume);
    uint logicalColor = (logical.x + logical.y + logical.z) & 1u;
    return logicalColor == SimpleDdgiSolveTargetColor(flags);
}

bool SimpleDdgiSolveVolumeMatches(
    uint flags,
    uint volumeIndex,
    uint filteredVolumeIndex)
{
    return (flags & SIMPLE_DDGI_SOLVE_VOLUME_FILTER) == 0u ||
        volumeIndex == filteredVolumeIndex;
}

bool SimpleDdgiSolveIsFirstColor(uint flags)
{
    return (flags & SIMPLE_DDGI_SOLVE_COLOR_FILTER) == 0u ||
        (flags & SIMPLE_DDGI_SOLVE_FIRST_COLOR) != 0u;
}

bool SimpleDdgiSolveIsFinalColor(uint flags)
{
    return (flags & SIMPLE_DDGI_SOLVE_COLOR_FILTER) == 0u ||
        (flags & SIMPLE_DDGI_SOLVE_FINAL_COLOR) != 0u;
}

bool SimpleDdgiSolveIsFinalSweep(uint flags, uint acceleratedSweepCount)
{
    // The unfiltered source/update path is a complete transaction in one
    // dispatch. Accelerated checkerboard work is complete when the probe's
    // matching color executes in the configured final sweep; tying completion
    // to the globally final color would permanently exclude the other parity.
    if ((flags & SIMPLE_DDGI_SOLVE_COLOR_FILTER) == 0u)
        return true;
    uint sweepIndex = flags >> SIMPLE_DDGI_SOLVE_SWEEP_INDEX_SHIFT;
    return sweepIndex + 1u >= max(acceleratedSweepCount, 1u);
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

// V2's certified estimator is shared by the regular cached-source blend and
// the frozen audit. It is deliberately positive and normalized by sampled
// cosine mass rather than using the signed reduced-SH reconstruction.
float SimpleDdgiPositiveCosineWeight(vec3 texelDirection, vec3 sampleDirection)
{
    float directionLengthSquared = dot(sampleDirection, sampleDirection);
    if (isnan(directionLengthSquared) || isinf(directionLengthSquared) ||
        directionLengthSquared <= 0.0000001)
    {
        return 0.0;
    }
    return max(
        dot(texelDirection, sampleDirection * inversesqrt(directionLengthSquared)),
        0.0);
}

vec3 SimpleDdgiEvaluatePositiveIrradiance(vec3 accumulated, float weightSum)
{
    if (any(isnan(accumulated)) || any(isinf(accumulated)) ||
        any(lessThan(accumulated, vec3(0.0))) ||
        isnan(weightSum) || isinf(weightSum) || weightSum <= 0.000001)
    {
        return vec3(0.0);
    }
    return accumulated * (SIMPLE_DDGI_PI / weightSum);
}

// The cached transport and the frozen audit must project the same bounded
// radiance. This radial luminance clamp is non-expansive on the nonnegative
// field and prevents an FP32 recursive spike from diverging from the value
// that the transport scratch/irradiance blend actually consumes.
vec3 SimpleDdgiClampTransportRadiance(vec3 value)
{
    if (any(isnan(value)) || any(isinf(value)))
        return vec3(0.0);
    vec3 nonnegative = max(value, vec3(0.0));
    float peak = max(nonnegative.x, max(nonnegative.y, nonnegative.z));
    if (peak <= 0.0)
        return vec3(0.0);
    // Normalize before the dot so an otherwise finite HDR input cannot
    // overflow the luminance reduction and collapse to black.
    float normalizedLuminance = dot(
        nonnegative / peak,
        vec3(0.2126, 0.7152, 0.0722));
    float scale = normalizedLuminance > 0.0 &&
        peak > 64.0 / normalizedLuminance
            ? (64.0 / peak) / normalizedLuminance
            : 1.0;
    return clamp(value * scale, vec3(0.0), vec3(65504.0));
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

// Fixed binary32 quaternion codebook. The table is generated once from the
// integer hash/Shoemake construction documented by the C# mirror and checked
// in as IEEE payloads so no runtime platform math can change an epoch.
const uvec4 SIMPLE_DDGI_DIRECTION_ROTATION_BITS[32] = uvec4[](
    uvec4(0xbf5bd755u, 0xbebab075u, 0x3ca7887cu, 0x3eb80372u),
    uvec4(0x3e6cc936u, 0xbf4054f9u, 0x3f161892u, 0x3e4874c2u),
    uvec4(0x3eac774au, 0xbf4c3fd2u, 0x3edfc806u, 0xbe78992fu),
    uvec4(0x3e8e3aa2u, 0xbe75df8fu, 0x3f1f4b91u, 0xbf30fd68u),
    uvec4(0xbeb0bf0cu, 0xbe3a07e6u, 0x3f338432u, 0x3f18c3ccu),
    uvec4(0xbebc6ba9u, 0xbe69227cu, 0x3eba2b0eu, 0x3f532f2bu),
    uvec4(0xbed7e49au, 0x3f01d379u, 0x3e9871c4u, 0xbf30b056u),
    uvec4(0x3f0f09f9u, 0xbe6572e7u, 0x3e28c577u, 0x3f480354u),
    uvec4(0x3ea53a7bu, 0xbf5c3e38u, 0xbebe24a5u, 0xbe088ba0u),
    uvec4(0xbf0abee3u, 0x3e42f1d1u, 0xbf093229u, 0x3f1e641bu),
    uvec4(0x3f4eee35u, 0x3f148c02u, 0xbdc995a8u, 0x3c74e75bu),
    uvec4(0x3f2fb909u, 0xbe9f1263u, 0xbe9a9b07u, 0x3f1584c6u),
    uvec4(0xbda8356eu, 0xbf1498a0u, 0x3f4d1e76u, 0xbdf52ebdu),
    uvec4(0xbed91c5au, 0xbf1f9b16u, 0xbf00b171u, 0x3ed87aa3u),
    uvec4(0xbba366acu, 0xbf370fa7u, 0x3ea02148u, 0x3f200a13u),
    uvec4(0xbc8c26bbu, 0xbee85062u, 0xbf640302u, 0x3cbd5cc1u),
    uvec4(0x3f026b2au, 0xbe84e2e1u, 0xbf4535b3u, 0xbe9081f2u),
    uvec4(0xbe041713u, 0xbe06180fu, 0xbf74c7c8u, 0x3e69623cu),
    uvec4(0x3e307ec5u, 0xbef062b7u, 0x3f394a01u, 0x3ef36619u),
    uvec4(0xbcd7d778u, 0xbf0aebdeu, 0x3e9b4dcdu, 0xbf486747u),
    uvec4(0x3f2314c5u, 0xbf344390u, 0x3e81d358u, 0x3e3cf7b2u),
    uvec4(0x3f5193d1u, 0x3c92ceb4u, 0xbf123561u, 0xbd6aca1du),
    uvec4(0xbeff1a77u, 0x3f072cd2u, 0xbf1bfc4fu, 0xbea3406cu),
    uvec4(0x3f34a557u, 0x3e3d736au, 0xbf053cb7u, 0x3ee33966u),
    uvec4(0x3f1f64ccu, 0xbea0bbd6u, 0xbf03a6b4u, 0xbeffa577u),
    uvec4(0x3e7d6bcau, 0x3e92524cu, 0x3ed236b9u, 0x3f546b76u),
    uvec4(0xbec9e9a1u, 0x3f314df4u, 0xbe8ce5b0u, 0x3f09a310u),
    uvec4(0x3eb479e5u, 0xbf31a3e3u, 0xbe9581c3u, 0xbf0e4c85u),
    uvec4(0x3ebf3f88u, 0x3e34d2f2u, 0x3ee4f4cfu, 0xbf4b1591u),
    uvec4(0x3f3c12d0u, 0x3d8ae5fcu, 0xbe99b488u, 0x3f1ac77au),
    uvec4(0xbf321385u, 0xbf02c48du, 0xbee07239u, 0xbe808a35u),
    uvec4(0xbea78718u, 0xbef28400u, 0xbf50f78bu, 0x3d434881u));

uint SimpleDdgiDirectionEpoch(uint sourceEpoch)
{
    return sourceEpoch & 0x1fu;
}

vec4 SimpleDdgiDirectionCodebookRotation(uint directionEpoch)
{
    return uintBitsToFloat(
        SIMPLE_DDGI_DIRECTION_ROTATION_BITS[directionEpoch & 0x1fu]);
}

uint SimpleDdgiUpdateDirectionRayIndex(
    uint localRayOrdinal,
    uint activeRayCount,
    uint sourceRayCount,
    SimpleDdgiParams p)
{
    return SimpleDdgiStorageDirectionRayIndex(
        localRayOrdinal,
        activeRayCount,
        sourceRayCount,
        p.raysPerProbe);
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

uint SimpleDdgiIrradianceAtlasWord(
    uint probeIndex,
    uint texelIndex,
    uint texelsPerProbe)
{
    return (probeIndex * texelsPerProbe * texelsPerProbe + texelIndex) * 2u;
}

uint SimpleDdgiVisibilityAtlasWord(
    uint probeIndex,
    uint texelIndex,
    uint texelsPerProbe)
{
    return probeIndex * texelsPerProbe * texelsPerProbe + texelIndex;
}

// Persistent V2 source cache: one entry per physical probe slot and
// deterministic full-sequence ray direction.  `directionRayIndex`, rather than
// the queue-local maintenance ray index, is the key so a low-rate maintenance
// subset reuses exactly the source ray that was originally traced.
const uint SIMPLE_DDGI_TRANSPORT_CACHE_VALID_FLAG = 1u << 24u;
const uint SIMPLE_DDGI_TRANSPORT_CACHE_HIT_FLAG = 1u << 25u;
const uint SIMPLE_DDGI_TRANSPORT_CACHE_BACKFACE_FLAG = 1u << 26u;
const uint SIMPLE_DDGI_TRANSPORT_CACHE_DIRECTION_EPOCH_SHIFT = 27u;
const uint SIMPLE_DDGI_TRANSPORT_CACHE_DIRECTION_EPOCH_MASK =
    0x1fu << SIMPLE_DDGI_TRANSPORT_CACHE_DIRECTION_EPOCH_SHIFT;
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
    // The high byte of the packed transmission word carries the complete
    // source sequence cardinality.  The RGB transmission payload remains
    // unchanged; retaining this per-probe value in every cache entry lets the
    // frozen audit evaluate mixed-ring fields without a second metadata SSBO.
    uint sourceRayCount;
    uint generationAndFlags;
    uint sourceLightingGeneration;
    uint sourceEpoch;
};

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

vec3 SimpleDdgiReconstructRayDirection(
    uint probeIndex,
    uint directionRayIndex,
    uint sourceEpoch,
    SimpleDdgiParams p)
{
    vec4 rotation = SimpleDdgiPerProbeRayRotation(
        probeIndex,
        SimpleDdgiDirectionCodebookRotation(
            SimpleDdgiDirectionEpoch(sourceEpoch)));
    vec3 direction = SimpleDdgiFibonacciDirection(
        directionRayIndex,
        p.raysPerProbe,
        rotation);
    // Match the former persistent octahedral SNORM16 representation exactly.
    return UnpackSimpleDdgiTransportOctDirection(
        PackSimpleDdgiTransportOctDirection(direction));
}

uint SimpleDdgiRayResultStrideWords(SimpleDdgiVolume volume)
{
#if SIMPLE_DDGI_STORAGE_SHADER_MODE == SIMPLE_DDGI_STORAGE_SHADER_MODE_PACKED
    return SIMPLE_DDGI_RAY_RESULT_STRIDE_WORDS;
#elif SIMPLE_DDGI_STORAGE_SHADER_MODE == SIMPLE_DDGI_STORAGE_SHADER_MODE_LEGACY
    return SIMPLE_DDGI_LEGACY_RAY_RESULT_STRIDE_WORDS;
#else
    uint abi = (volume.cacheLayoutFlags & SIMPLE_DDGI_CACHE_ABI_MASK) >>
        SIMPLE_DDGI_CACHE_ABI_SHIFT;
    return abi == SIMPLE_DDGI_TRANSPORT_RAY_CACHE_ABI_VERSION
        ? SIMPLE_DDGI_RAY_RESULT_STRIDE_WORDS
        : SIMPLE_DDGI_LEGACY_RAY_RESULT_STRIDE_WORDS;
#endif
}

void ClearSimpleDdgiRayResultStorage(
    uint bufferIndex,
    uint rayResultIndex,
    SimpleDdgiVolume volume)
{
    uint stride = SimpleDdgiRayResultStrideWords(volume);
    uint baseWord = rayResultIndex * stride;
    WriteStorageVec4Uniform(bufferIndex, baseWord, vec4(0.0));
#if SIMPLE_DDGI_STORAGE_SHADER_MODE == SIMPLE_DDGI_STORAGE_SHADER_MODE_PACKED
    WriteStorageWordUniform(bufferIndex, baseWord + 4u, 0u);
#elif SIMPLE_DDGI_STORAGE_SHADER_MODE == SIMPLE_DDGI_STORAGE_SHADER_MODE_LEGACY
    WriteStorageVec4Uniform(bufferIndex, baseWord + 4u, vec4(0.0));
#else
    if (stride == SIMPLE_DDGI_RAY_RESULT_STRIDE_WORDS)
        WriteStorageWordUniform(bufferIndex, baseWord + 4u, 0u);
    else
        WriteStorageVec4Uniform(bufferIndex, baseWord + 4u, vec4(0.0));
#endif
}

// The ordered trace-reuse kernel clears every dispatched record before trying
// the persistent cache, then publishes the payload and validity marker through
// a compute barrier. The source kernel only needs this compact handshake to
// distinguish a completed reuse from a cache miss; downstream consumers still
// perform the full payload validation below.
bool SimpleDdgiRayResultStorageIsPopulatedForEpoch(
    uint bufferIndex,
    uint rayResultIndex,
    SimpleDdgiVolume volume,
    uint expectedSourceEpoch)
{
    if (bufferIndex == 0u || expectedSourceEpoch == 0u)
        return false;
    uint stride = SimpleDdgiRayResultStrideWords(volume);
    uint baseWord = rayResultIndex * stride;
#if SIMPLE_DDGI_STORAGE_SHADER_MODE == SIMPLE_DDGI_STORAGE_SHADER_MODE_PACKED
    uint metadata = ReadStorageWordUniform(bufferIndex, baseWord + 4u);
    vec2 visibilityHit;
    uint directionEpoch;
    return UnpackSimpleDdgiRayMetadata(
            metadata,
            visibilityHit,
            directionEpoch) &&
        directionEpoch == SimpleDdgiDirectionEpoch(expectedSourceEpoch);
#elif SIMPLE_DDGI_STORAGE_SHADER_MODE == SIMPLE_DDGI_STORAGE_SHADER_MODE_LEGACY
    vec3 direction = ReadStorageVec4Uniform(bufferIndex, baseWord + 4u).xyz;
    return !any(isnan(direction)) && !any(isinf(direction)) &&
        dot(direction, direction) > 1.0e-12;
#else
    if (stride == SIMPLE_DDGI_RAY_RESULT_STRIDE_WORDS)
    {
        uint metadata = ReadStorageWordUniform(bufferIndex, baseWord + 4u);
        vec2 visibilityHit;
        uint directionEpoch;
        return UnpackSimpleDdgiRayMetadata(
                metadata,
                visibilityHit,
                directionEpoch) &&
            directionEpoch == SimpleDdgiDirectionEpoch(expectedSourceEpoch);
    }
    vec3 direction = ReadStorageVec4Uniform(bufferIndex, baseWord + 4u).xyz;
    return !any(isnan(direction)) && !any(isinf(direction)) &&
        dot(direction, direction) > 1.0e-12;
#endif
}

void WriteSimpleDdgiRayResultStorage(
    uint bufferIndex,
    uint rayResultIndex,
    SimpleDdgiVolume volume,
    SimpleDdgiParams p,
    uint probeIndex,
    uint directionRayIndex,
    uint sourceEpoch,
    vec3 radiance,
    float surfaceDistance,
    float visibilityDistance,
    vec3 direction,
    float hitKind)
{
    uint stride = SimpleDdgiRayResultStrideWords(volume);
    uint baseWord = rayResultIndex * stride;
    bool invalidSourceEpoch = sourceEpoch == 0u;
    bool invalidHitKind = isnan(hitKind) || isinf(hitKind) ||
        hitKind < SIMPLE_DDGI_RAY_HIT_KIND_MISS ||
        hitKind > SIMPLE_DDGI_RAY_HIT_KIND_FAR_FIELD_BACK_FACE ||
        hitKind != round(hitKind);
    bool validPayload = !invalidSourceEpoch && !invalidHitKind &&
        !any(isnan(radiance)) && !any(isinf(radiance)) &&
        !any(lessThan(radiance, vec3(0.0))) &&
        !isnan(surfaceDistance) && !isinf(surfaceDistance) &&
        surfaceDistance >= 0.0 &&
        !isnan(visibilityDistance) && !isinf(visibilityDistance) &&
        visibilityDistance >= 0.0 &&
        !any(isnan(direction)) && !any(isinf(direction)) &&
        dot(direction, direction) > 1.0e-12;
    if (!validPayload)
    {
        // Clear the whole record, including the validity/epoch word. A failed
        // producer must not leave an older queue entry looking publishable.
        WriteStorageVec4Uniform(bufferIndex, baseWord, vec4(0.0));
#if SIMPLE_DDGI_STORAGE_SHADER_MODE == SIMPLE_DDGI_STORAGE_SHADER_MODE_PACKED
        WriteStorageWordUniform(bufferIndex, baseWord + 4u, 0u);
#elif SIMPLE_DDGI_STORAGE_SHADER_MODE == SIMPLE_DDGI_STORAGE_SHADER_MODE_LEGACY
        WriteStorageVec4Uniform(bufferIndex, baseWord + 4u, vec4(0.0));
#else
        if (stride == SIMPLE_DDGI_RAY_RESULT_STRIDE_WORDS)
            WriteStorageWordUniform(bufferIndex, baseWord + 4u, 0u);
        else
            WriteStorageVec4Uniform(bufferIndex, baseWord + 4u, vec4(0.0));
#endif
        RecordSimpleDdgiInvalidRayMetadata(
            p, invalidSourceEpoch, invalidHitKind);
        return;
    }
    WriteStorageVec4Uniform(
        bufferIndex,
        baseWord,
        vec4(
            clamp(radiance, vec3(0.0), vec3(65504.0)),
            max(surfaceDistance, 0.0)));
#if SIMPLE_DDGI_STORAGE_SHADER_MODE == SIMPLE_DDGI_STORAGE_SHADER_MODE_PACKED
    WriteStorageWordUniform(
        bufferIndex,
        baseWord + 4u,
        PackSimpleDdgiRayMetadata(visibilityDistance, hitKind, sourceEpoch));
#elif SIMPLE_DDGI_STORAGE_SHADER_MODE == SIMPLE_DDGI_STORAGE_SHADER_MODE_LEGACY
    WriteStorageFloatUniform(bufferIndex, baseWord + 4u, direction.x);
    WriteStorageFloatUniform(bufferIndex, baseWord + 5u, direction.y);
    WriteStorageFloatUniform(bufferIndex, baseWord + 6u, direction.z);
    WriteStorageWordUniform(
        bufferIndex,
        baseWord + 7u,
        PackSimpleDdgiRayVisibilityHit(visibilityDistance, hitKind));
#else
    if (stride == SIMPLE_DDGI_RAY_RESULT_STRIDE_WORDS)
    {
        WriteStorageWordUniform(
            bufferIndex,
            baseWord + 4u,
            PackSimpleDdgiRayMetadata(
                visibilityDistance,
                hitKind,
                sourceEpoch));
    }
    else
    {
        WriteStorageFloatUniform(bufferIndex, baseWord + 4u, direction.x);
        WriteStorageFloatUniform(bufferIndex, baseWord + 5u, direction.y);
        WriteStorageFloatUniform(bufferIndex, baseWord + 6u, direction.z);
        WriteStorageWordUniform(
            bufferIndex,
            baseWord + 7u,
            PackSimpleDdgiRayVisibilityHit(visibilityDistance, hitKind));
    }
#endif
}

bool ReadSimpleDdgiRayResultStorage(
    uint bufferIndex,
    uint rayResultIndex,
    SimpleDdgiVolume volume,
    SimpleDdgiParams p,
    uint probeIndex,
    uint directionRayIndex,
    uint expectedSourceEpoch,
    out vec4 radianceDistance,
    out vec3 direction,
    out vec2 visibilityHit,
    out uint directionEpoch)
{
    if (expectedSourceEpoch == 0u)
    {
        RecordSimpleDdgiInvalidRayMetadata(p, true, false);
        radianceDistance = vec4(0.0);
        direction = vec3(0.0, 1.0, 0.0);
        visibilityHit = vec2(0.0);
        directionEpoch = 0u;
        return false;
    }
    uint stride = SimpleDdgiRayResultStrideWords(volume);
    uint baseWord = rayResultIndex * stride;
    // Packed scratch has a five-word stride, so only every fourth record is
    // naturally vec4-aligned. Use the arbitrary-word accessor for the payload
    // instead of rounding baseWord down through the aligned vector view.
    radianceDistance = ReadStorageVec4Uniform(bufferIndex, baseWord);
    directionEpoch = SimpleDdgiDirectionEpoch(expectedSourceEpoch);
#if SIMPLE_DDGI_STORAGE_SHADER_MODE == SIMPLE_DDGI_STORAGE_SHADER_MODE_PACKED
    uint metadata = ReadStorageWordUniform(bufferIndex, baseWord + 4u);
    bool metadataValid = UnpackSimpleDdgiRayMetadata(
        metadata,
        visibilityHit,
        directionEpoch);
    bool epochMatches = directionEpoch ==
        SimpleDdgiDirectionEpoch(expectedSourceEpoch);
    if (!metadataValid || !epochMatches)
    {
        if (metadataValid && !epochMatches)
            RecordSimpleDdgiDirectionEpochMismatch(p);
        direction = vec3(0.0, 1.0, 0.0);
        return false;
    }
    direction = SimpleDdgiReconstructRayDirection(
        probeIndex,
        directionRayIndex,
        directionEpoch,
        p);
#elif SIMPLE_DDGI_STORAGE_SHADER_MODE == SIMPLE_DDGI_STORAGE_SHADER_MODE_LEGACY
    direction = ReadStorageVec4Uniform(bufferIndex, baseWord + 4u).xyz;
    visibilityHit = UnpackSimpleDdgiRayVisibilityHit(
        ReadStorageWordUniform(bufferIndex, baseWord + 7u));
#else
    if (stride == SIMPLE_DDGI_RAY_RESULT_STRIDE_WORDS)
    {
        uint metadata = ReadStorageWordUniform(bufferIndex, baseWord + 4u);
        bool metadataValid = UnpackSimpleDdgiRayMetadata(
            metadata,
            visibilityHit,
            directionEpoch);
        bool epochMatches = directionEpoch ==
            SimpleDdgiDirectionEpoch(expectedSourceEpoch);
        if (!metadataValid || !epochMatches)
        {
            if (metadataValid && !epochMatches)
                RecordSimpleDdgiDirectionEpochMismatch(p);
            direction = vec3(0.0, 1.0, 0.0);
            return false;
        }
        direction = SimpleDdgiReconstructRayDirection(
            probeIndex,
            directionRayIndex,
            directionEpoch,
            p);
    }
    else
    {
        direction = ReadStorageVec4Uniform(
            bufferIndex,
            baseWord + 4u).xyz;
        visibilityHit = UnpackSimpleDdgiRayVisibilityHit(
            ReadStorageWordUniform(bufferIndex, baseWord + 7u));
    }
#endif
    bool invalidHitKind = isnan(visibilityHit.y) || isinf(visibilityHit.y) ||
        visibilityHit.y < SIMPLE_DDGI_RAY_HIT_KIND_MISS ||
        visibilityHit.y > SIMPLE_DDGI_RAY_HIT_KIND_FAR_FIELD_BACK_FACE ||
        visibilityHit.y != round(visibilityHit.y);
    if (invalidHitKind)
        RecordSimpleDdgiInvalidRayMetadata(p, false, true);
    return !any(isnan(radianceDistance)) &&
        !any(isinf(radianceDistance)) &&
        !any(lessThan(radianceDistance.rgb, vec3(0.0))) &&
        radianceDistance.w >= 0.0 &&
        !any(isnan(direction)) && !any(isinf(direction)) &&
        dot(direction, direction) > 1.0e-12 &&
        !any(isnan(visibilityHit)) && !any(isinf(visibilityHit)) &&
        visibilityHit.x >= 0.0 && !invalidHitKind;
}

void WriteSimpleDdgiTransportRayCache(
    uint bufferIndex,
    uint cacheProbeBaseWordPlusOne,
    uint volumeIndex,
    uint physicalProbeIndex,
    uint directionRayIndex,
    SimpleDdgiVolume volume,
    SimpleDdgiParams p,
    vec3 sourceRadiance,
    float distance,
    vec3 direction,
    vec3 normal,
    vec3 diffuseReflectance,
    vec3 transmittedDiffuseReflectance,
    float materialOcclusion,
    float hitKind,
    uint probeGeneration,
    uint sourceLightingGeneration,
    uint sourceEpoch,
    uint sourceRayCount)
{
    uint baseWord;
    uint format;
    if (bufferIndex == 0u ||
        !TryResolveSimpleDdgiTransportCacheAddressFromProbeBase(
            cacheProbeBaseWordPlusOne,
            directionRayIndex,
            p.raysPerProbe,
            volume.cacheStrideWords,
            volume.cacheLayoutFlags,
            baseWord,
            format))
    {
        return;
    }
    uint generationWord = baseWord +
        SimpleDdgiStorageExpectedStride(format) - 1u;
    bool invalidSourceEpoch = sourceEpoch == 0u;
    bool invalidHitKind = isnan(hitKind) || isinf(hitKind) ||
        hitKind < SIMPLE_DDGI_RAY_HIT_KIND_MISS ||
        hitKind > SIMPLE_DDGI_RAY_HIT_KIND_FAR_FIELD_BACK_FACE ||
        hitKind != round(hitKind);
    bool payloadFinite =
        !any(isnan(sourceRadiance)) && !any(isinf(sourceRadiance)) &&
        !any(lessThan(sourceRadiance, vec3(0.0))) &&
        !isnan(distance) && !isinf(distance) && distance >= 0.0 &&
        !any(isnan(direction)) && !any(isinf(direction)) &&
        dot(direction, direction) > 1.0e-12 &&
        !any(isnan(normal)) && !any(isinf(normal)) &&
        dot(normal, normal) > 1.0e-12 &&
        !any(isnan(diffuseReflectance)) &&
        !any(isinf(diffuseReflectance)) &&
        !any(lessThan(diffuseReflectance, vec3(0.0))) &&
        !any(isnan(transmittedDiffuseReflectance)) &&
        !any(isinf(transmittedDiffuseReflectance)) &&
        !any(lessThan(transmittedDiffuseReflectance, vec3(0.0))) &&
        !isnan(materialOcclusion) && !isinf(materialOcclusion) &&
        !invalidHitKind &&
        (probeGeneration & SIMPLE_DDGI_UPDATE_GENERATION_MASK) != 0u &&
        sourceLightingGeneration != 0u && !invalidSourceEpoch &&
        sourceRayCount >= 1u && sourceRayCount <= p.raysPerProbe;
    if (!payloadFinite)
    {
        RecordSimpleDdgiCachePackAttempt(
            p,
            volumeIndex,
            physicalProbeIndex,
            directionRayIndex,
            false,
            false,
            0.0,
            0.0);
        WriteStorageWordUniform(bufferIndex, generationWord, 0u);
        RecordSimpleDdgiInvalidRayMetadata(
            p, invalidSourceEpoch, invalidHitKind);
        return;
    }
    vec3 packedSourceRadiance =
        SimpleDdgiClampTransportRadiance(sourceRadiance);
    uint packedRadianceRg = 0u;
    uint packedRadianceBAndDistance = 0u;
    vec3 decodedRadiance;
    float decodedDistance;
#if SIMPLE_DDGI_STORAGE_SHADER_MODE == SIMPLE_DDGI_STORAGE_SHADER_MODE_LEGACY
    WriteStorageVec4Uniform(
        bufferIndex,
        baseWord,
        vec4(packedSourceRadiance, distance));
    decodedRadiance = packedSourceRadiance;
    decodedDistance = distance;
#elif SIMPLE_DDGI_STORAGE_SHADER_MODE == SIMPLE_DDGI_STORAGE_SHADER_MODE_PACKED
    packedRadianceRg = packHalf2x16(packedSourceRadiance.xy);
    if (format == SIMPLE_DDGI_CACHE_FORMAT_COMPACT_28)
    {
        packedRadianceBAndDistance = packHalf2x16(vec2(
            packedSourceRadiance.z, 0.0));
        WriteStorageWordUniform(bufferIndex, baseWord, packedRadianceRg);
        WriteStorageWordUniform(
            bufferIndex, baseWord + 1u, packedRadianceBAndDistance);
        WriteStorageWordUniform(
            bufferIndex, baseWord + 2u, floatBitsToUint(distance));
        decodedDistance = distance;
    }
    else
    {
        packedRadianceBAndDistance = packHalf2x16(vec2(
            packedSourceRadiance.z,
            clamp(distance, 0.0, 65504.0)));
        WriteStorageWordUniform(bufferIndex, baseWord, packedRadianceRg);
        WriteStorageWordUniform(
            bufferIndex, baseWord + 1u, packedRadianceBAndDistance);
        decodedDistance = unpackHalf2x16(packedRadianceBAndDistance).y;
    }
    decodedRadiance = vec3(
        unpackHalf2x16(packedRadianceRg),
        unpackHalf2x16(packedRadianceBAndDistance).x);
#else
    if (format == SIMPLE_DDGI_CACHE_FORMAT_LEGACY_36)
    {
        WriteStorageVec4Uniform(
            bufferIndex,
            baseWord,
            vec4(packedSourceRadiance, distance));
        decodedRadiance = packedSourceRadiance;
        decodedDistance = distance;
    }
    else if (format == SIMPLE_DDGI_CACHE_FORMAT_COMPACT_28)
    {
        packedRadianceRg = packHalf2x16(packedSourceRadiance.xy);
        packedRadianceBAndDistance = packHalf2x16(vec2(
            packedSourceRadiance.z, 0.0));
        WriteStorageWordUniform(bufferIndex, baseWord, packedRadianceRg);
        WriteStorageWordUniform(bufferIndex, baseWord + 1u,
            packedRadianceBAndDistance);
        WriteStorageWordUniform(
            bufferIndex,
            baseWord + 2u,
            floatBitsToUint(distance));
        decodedRadiance = vec3(
            unpackHalf2x16(packedRadianceRg),
            unpackHalf2x16(packedRadianceBAndDistance).x);
        decodedDistance = distance;
    }
    else
    {
        packedRadianceRg = packHalf2x16(packedSourceRadiance.xy);
        packedRadianceBAndDistance = packHalf2x16(vec2(
            packedSourceRadiance.z,
            clamp(distance, 0.0, 65504.0)));
        WriteStorageWordUniform(bufferIndex, baseWord, packedRadianceRg);
        WriteStorageWordUniform(bufferIndex, baseWord + 1u,
            packedRadianceBAndDistance);
        decodedRadiance = vec3(
            unpackHalf2x16(packedRadianceRg),
            unpackHalf2x16(packedRadianceBAndDistance).x);
        decodedDistance = unpackHalf2x16(
            packedRadianceBAndDistance).y;
    }
#endif
    vec3 radianceError = abs(decodedRadiance - sourceRadiance);
    RecordSimpleDdgiCachePackAttempt(
        p,
        volumeIndex,
        physicalProbeIndex,
        directionRayIndex,
        true,
        any(greaterThan(abs(packedSourceRadiance - sourceRadiance),
            vec3(0.000001))),
        max(radianceError.x, max(radianceError.y, radianceError.z)),
        abs(decodedDistance - distance));
#if SIMPLE_DDGI_STORAGE_SHADER_MODE == SIMPLE_DDGI_STORAGE_SHADER_MODE_LEGACY
    uint normalWord = baseWord + 5u;
#elif SIMPLE_DDGI_STORAGE_SHADER_MODE == SIMPLE_DDGI_STORAGE_SHADER_MODE_PACKED
    uint normalWord = format == SIMPLE_DDGI_CACHE_FORMAT_COMPACT_28
        ? baseWord + 3u
        : baseWord + 2u;
#else
    uint normalWord = format == SIMPLE_DDGI_CACHE_FORMAT_LEGACY_36
        ? baseWord + 5u
        : format == SIMPLE_DDGI_CACHE_FORMAT_COMPACT_28
            ? baseWord + 3u
            : baseWord + 2u;
#endif
    uint materialWord = normalWord + 1u;
    uint transmissionWord = normalWord + 2u;
#if SIMPLE_DDGI_STORAGE_SHADER_MODE == SIMPLE_DDGI_STORAGE_SHADER_MODE_LEGACY
    WriteStorageWordUniform(
        bufferIndex,
        baseWord + 4u,
        PackSimpleDdgiTransportOctDirection(direction));
#elif SIMPLE_DDGI_STORAGE_SHADER_MODE == SIMPLE_DDGI_STORAGE_SHADER_MODE_DYNAMIC
    if (format == SIMPLE_DDGI_CACHE_FORMAT_LEGACY_36)
        WriteStorageWordUniform(
            bufferIndex,
            baseWord + 4u,
            PackSimpleDdgiTransportOctDirection(direction));
#endif
    WriteStorageWordUniform(bufferIndex, normalWord, PackSimpleDdgiTransportOctDirection(normal));
    WriteStorageWordUniform(
        bufferIndex,
        materialWord,
        packUnorm4x8(vec4(
            clamp(diffuseReflectance, vec3(0.0), vec3(1.0)),
            clamp(materialOcclusion, 0.0, 1.0))));
    uint packedTransmission = packUnorm4x8(vec4(
        clamp(transmittedDiffuseReflectance, vec3(0.0), vec3(1.0)),
        0.0));
#if SIMPLE_DDGI_STORAGE_SHADER_MODE == SIMPLE_DDGI_STORAGE_SHADER_MODE_LEGACY
    uint encodedSourceRayCount = sourceRayCount >= 256u
        ? 0u
        : clamp(sourceRayCount, 1u, 255u);
    packedTransmission = (packedTransmission & 0x00ffffffu) |
        (encodedSourceRayCount << 24u);
#elif SIMPLE_DDGI_STORAGE_SHADER_MODE == SIMPLE_DDGI_STORAGE_SHADER_MODE_DYNAMIC
    if (format == SIMPLE_DDGI_CACHE_FORMAT_LEGACY_36)
    {
        uint encodedSourceRayCount = sourceRayCount >= 256u
            ? 0u
            : clamp(sourceRayCount, 1u, 255u);
        packedTransmission = (packedTransmission & 0x00ffffffu) |
            (encodedSourceRayCount << 24u);
    }
#endif
    WriteStorageWordUniform(bufferIndex, transmissionWord, packedTransmission);
    uint flags = SIMPLE_DDGI_TRANSPORT_CACHE_VALID_FLAG |
        (hitKind >= 0.5 ? SIMPLE_DDGI_TRANSPORT_CACHE_HIT_FLAG : 0u) |
        (hitKind > 1.5 ? SIMPLE_DDGI_TRANSPORT_CACHE_BACKFACE_FLAG : 0u) |
        (SimpleDdgiDirectionEpoch(sourceEpoch) <<
            SIMPLE_DDGI_TRANSPORT_CACHE_DIRECTION_EPOCH_SHIFT);
    WriteStorageWordUniform(
        bufferIndex,
        generationWord,
        (probeGeneration & SIMPLE_DDGI_UPDATE_GENERATION_MASK) | flags);
}

// Packed trace reuse consumes only source radiance, distance, and hit
// classification. This avoids lowering solver-only surface reconstruction into
// the already large ray-query kernel while retaining publication/identity gates.
bool ReadSimpleDdgiPackedTransportRayCacheForTrace(
    uint bufferIndex,
    uint cacheProbeBaseWordPlusOne,
    uint directionRayIndex,
    SimpleDdgiVolume volume,
    SimpleDdgiParams p,
    uint expectedProbeGeneration,
    uint expectedSourceLightingGeneration,
    uint expectedSourceEpoch,
    uint expectedSourceRayCount,
    out vec4 sourceRadianceDistance,
    out float hitKind)
{
    sourceRadianceDistance = vec4(0.0);
    hitKind = SIMPLE_DDGI_RAY_HIT_KIND_MISS;
    if (bufferIndex == 0u || expectedProbeGeneration == 0u ||
        expectedSourceLightingGeneration == 0u || expectedSourceEpoch == 0u ||
        expectedSourceRayCount == 0u || expectedSourceRayCount > p.raysPerProbe)
    {
        return false;
    }

    uint baseWord;
    uint format;
    if (!TryResolveSimpleDdgiTransportCacheAddressFromProbeBase(
            cacheProbeBaseWordPlusOne,
            directionRayIndex,
            p.raysPerProbe,
            volume.cacheStrideWords,
            volume.cacheLayoutFlags,
            baseWord,
            format) ||
        (format != SIMPLE_DDGI_CACHE_FORMAT_COMPACT_28 &&
            format != SIMPLE_DDGI_CACHE_FORMAT_COMPACT_24))
    {
        return false;
    }

    uint generationWord = baseWord +
        SimpleDdgiStorageExpectedStride(format) - 1u;
    uint generationAndFlags = ReadStorageWordUniform(bufferIndex, generationWord);
    uint cachedDirectionEpoch = (generationAndFlags &
        SIMPLE_DDGI_TRANSPORT_CACHE_DIRECTION_EPOCH_MASK) >>
        SIMPLE_DDGI_TRANSPORT_CACHE_DIRECTION_EPOCH_SHIFT;
    bool epochMatches = cachedDirectionEpoch ==
        SimpleDdgiDirectionEpoch(expectedSourceEpoch);
    if ((generationAndFlags & SIMPLE_DDGI_TRANSPORT_CACHE_VALID_FLAG) == 0u ||
        (generationAndFlags & SIMPLE_DDGI_UPDATE_GENERATION_MASK) !=
            (expectedProbeGeneration & SIMPLE_DDGI_UPDATE_GENERATION_MASK) ||
        ((generationAndFlags & SIMPLE_DDGI_TRANSPORT_CACHE_BACKFACE_FLAG) != 0u &&
            (generationAndFlags & SIMPLE_DDGI_TRANSPORT_CACHE_HIT_FLAG) == 0u) ||
        !epochMatches)
    {
        if (!epochMatches)
            RecordSimpleDdgiDirectionEpochMismatch(p);
        return false;
    }

    uint packedRg = ReadStorageWordUniform(bufferIndex, baseWord);
    uint packedBAndDistance = ReadStorageWordUniform(bufferIndex, baseWord + 1u);
    if (format == SIMPLE_DDGI_CACHE_FORMAT_COMPACT_28 &&
        (packedBAndDistance & 0xffff0000u) != 0u)
    {
        return false;
    }
    vec2 radianceRg = unpackHalf2x16(packedRg);
    vec2 radianceBAndDistance = unpackHalf2x16(packedBAndDistance);
    sourceRadianceDistance = vec4(
        radianceRg,
        radianceBAndDistance.x,
        format == SIMPLE_DDGI_CACHE_FORMAT_COMPACT_28
            ? uintBitsToFloat(ReadStorageWordUniform(bufferIndex, baseWord + 2u))
            : radianceBAndDistance.y);
    uint transmissionWord = format == SIMPLE_DDGI_CACHE_FORMAT_COMPACT_28
        ? baseWord + 5u
        : baseWord + 4u;
    if ((ReadStorageWordUniform(bufferIndex, transmissionWord) & 0xff000000u) != 0u ||
        any(isnan(sourceRadianceDistance)) || any(isinf(sourceRadianceDistance)) ||
        any(lessThan(sourceRadianceDistance.rgb, vec3(0.0))) ||
        sourceRadianceDistance.w < 0.0)
    {
        return false;
    }

    hitKind = (generationAndFlags & SIMPLE_DDGI_TRANSPORT_CACHE_HIT_FLAG) == 0u
        ? SIMPLE_DDGI_RAY_HIT_KIND_MISS
        : (generationAndFlags & SIMPLE_DDGI_TRANSPORT_CACHE_BACKFACE_FLAG) != 0u
            ? SIMPLE_DDGI_RAY_HIT_KIND_ONE_SIDED_BACK_FACE
            : SIMPLE_DDGI_RAY_HIT_KIND_FRONT_FACE;
    return true;
}

// Legacy qualification mode uses the same queue-carried per-probe base as the
// packed path. Trace reuse needs only the source payload, stored direction, and
// hit classification; keeping solver-only surface decoding out of this kernel
// also avoids a known native-driver lowering failure in the rollback variant.
bool ReadSimpleDdgiLegacyTransportRayCacheForTrace(
    uint bufferIndex,
    uint cacheProbeBaseWordPlusOne,
    uint directionRayIndex,
    SimpleDdgiVolume volume,
    SimpleDdgiParams p,
    uint expectedProbeGeneration,
    uint expectedSourceLightingGeneration,
    uint expectedSourceEpoch,
    uint expectedSourceRayCount,
    out vec4 sourceRadianceDistance,
    out vec3 direction,
    out float hitKind)
{
    sourceRadianceDistance = vec4(0.0);
    direction = vec3(0.0, 1.0, 0.0);
    hitKind = SIMPLE_DDGI_RAY_HIT_KIND_MISS;
    if (bufferIndex == 0u || expectedProbeGeneration == 0u ||
        expectedSourceLightingGeneration == 0u || expectedSourceEpoch == 0u ||
        expectedSourceRayCount == 0u || expectedSourceRayCount > p.raysPerProbe)
    {
        return false;
    }

    uint baseWord;
    uint format;
    if (!TryResolveSimpleDdgiTransportCacheAddressFromProbeBase(
            cacheProbeBaseWordPlusOne,
            directionRayIndex,
            p.raysPerProbe,
            volume.cacheStrideWords,
            volume.cacheLayoutFlags,
            baseWord,
            format) ||
        format != SIMPLE_DDGI_CACHE_FORMAT_LEGACY_36)
    {
        return false;
    }

    uint generationAndFlags = ReadStorageWordUniform(bufferIndex, baseWord + 8u);
    uint cachedDirectionEpoch = (generationAndFlags &
        SIMPLE_DDGI_TRANSPORT_CACHE_DIRECTION_EPOCH_MASK) >>
        SIMPLE_DDGI_TRANSPORT_CACHE_DIRECTION_EPOCH_SHIFT;
    bool epochMatches = cachedDirectionEpoch ==
        SimpleDdgiDirectionEpoch(expectedSourceEpoch);
    if ((generationAndFlags & SIMPLE_DDGI_TRANSPORT_CACHE_VALID_FLAG) == 0u ||
        (generationAndFlags & SIMPLE_DDGI_UPDATE_GENERATION_MASK) !=
            (expectedProbeGeneration & SIMPLE_DDGI_UPDATE_GENERATION_MASK) ||
        ((generationAndFlags & SIMPLE_DDGI_TRANSPORT_CACHE_BACKFACE_FLAG) != 0u &&
            (generationAndFlags & SIMPLE_DDGI_TRANSPORT_CACHE_HIT_FLAG) == 0u) ||
        !epochMatches)
    {
        if (!epochMatches)
            RecordSimpleDdgiDirectionEpochMismatch(p);
        return false;
    }

    sourceRadianceDistance = ReadStorageVec4Uniform(bufferIndex, baseWord);
    direction = UnpackSimpleDdgiTransportOctDirection(
        ReadStorageWordUniform(bufferIndex, baseWord + 4u));
    uint encodedSourceRayCount =
        ReadStorageWordUniform(bufferIndex, baseWord + 7u) >> 24u;
    uint cachedSourceRayCount = encodedSourceRayCount == 0u
        ? 256u
        : encodedSourceRayCount;
    if (cachedSourceRayCount != expectedSourceRayCount ||
        any(isnan(sourceRadianceDistance)) ||
        any(isinf(sourceRadianceDistance)) ||
        any(lessThan(sourceRadianceDistance.rgb, vec3(0.0))) ||
        sourceRadianceDistance.w < 0.0 ||
        any(isnan(direction)) || any(isinf(direction)) ||
        dot(direction, direction) <= 1.0e-12)
    {
        return false;
    }

    hitKind = (generationAndFlags & SIMPLE_DDGI_TRANSPORT_CACHE_HIT_FLAG) == 0u
        ? SIMPLE_DDGI_RAY_HIT_KIND_MISS
        : (generationAndFlags & SIMPLE_DDGI_TRANSPORT_CACHE_BACKFACE_FLAG) != 0u
            ? SIMPLE_DDGI_RAY_HIT_KIND_ONE_SIDED_BACK_FACE
            : SIMPLE_DDGI_RAY_HIT_KIND_FRONT_FACE;
    return true;
}

// Complete legacy solver decode using the same producer-validated one-based
// cache address as trace and transport. Keeping sparse ownership resolution
// outside this function makes it suitable for the bounded audit ray kernel,
// whose reduction stage independently verifies the persisted address.
bool ReadSimpleDdgiLegacyTransportRayCacheForSolve(
    uint bufferIndex,
    uint cacheProbeBaseWordPlusOne,
    uint directionProbeIndex,
    uint directionRayIndex,
    SimpleDdgiVolume volume,
    SimpleDdgiParams p,
    uint expectedProbeGeneration,
    uint expectedSourceLightingGeneration,
    uint expectedSourceEpoch,
    uint expectedSourceRayCount,
    out SimpleDdgiTransportRayCache cache)
{
    cache.sourceRadiance = vec3(0.0);
    cache.distance = 0.0;
    cache.direction = vec3(0.0, 1.0, 0.0);
    cache.normal = vec3(0.0, 1.0, 0.0);
    cache.diffuseReflectance = vec3(0.0);
    cache.transmittedDiffuseReflectance = vec3(0.0);
    cache.materialOcclusion = 1.0;
    cache.sourceRayCount = 0u;
    cache.generationAndFlags = 0u;
    cache.sourceLightingGeneration = 0u;
    cache.sourceEpoch = 0u;

    if (bufferIndex == 0u || expectedProbeGeneration == 0u ||
        expectedSourceLightingGeneration == 0u || expectedSourceEpoch == 0u ||
        expectedSourceRayCount == 0u || expectedSourceRayCount > p.raysPerProbe)
    {
        return false;
    }

    // Resolve and validate the complete record once.  Apart from avoiding
    // duplicate storage-address work, this flat decode gives native drivers the
    // same bounded control-flow shape as the proven packed solver path.
    uint baseWord;
    uint format;
    if (!TryResolveSimpleDdgiTransportCacheAddressFromProbeBase(
            cacheProbeBaseWordPlusOne,
            directionRayIndex,
            p.raysPerProbe,
            volume.cacheStrideWords,
            volume.cacheLayoutFlags,
            baseWord,
            format) ||
        format != SIMPLE_DDGI_CACHE_FORMAT_LEGACY_36)
    {
        return false;
    }

    uint generationAndFlags = ReadStorageWordUniform(bufferIndex, baseWord + 8u);
    uint cachedDirectionEpoch = (generationAndFlags &
        SIMPLE_DDGI_TRANSPORT_CACHE_DIRECTION_EPOCH_MASK) >>
            SIMPLE_DDGI_TRANSPORT_CACHE_DIRECTION_EPOCH_SHIFT;
    bool epochMatches = cachedDirectionEpoch ==
        SimpleDdgiDirectionEpoch(expectedSourceEpoch);
    if ((generationAndFlags & SIMPLE_DDGI_TRANSPORT_CACHE_VALID_FLAG) == 0u ||
        (generationAndFlags & SIMPLE_DDGI_UPDATE_GENERATION_MASK) !=
            (expectedProbeGeneration & SIMPLE_DDGI_UPDATE_GENERATION_MASK) ||
        ((generationAndFlags & SIMPLE_DDGI_TRANSPORT_CACHE_BACKFACE_FLAG) != 0u &&
            (generationAndFlags & SIMPLE_DDGI_TRANSPORT_CACHE_HIT_FLAG) == 0u) ||
        !epochMatches)
    {
        if (!epochMatches)
            RecordSimpleDdgiDirectionEpochMismatch(p);
        return false;
    }

    // Use four uniform scalar loads here instead of a dynamically addressed
    // vec4 load.  The payload remains the same four FP32 words, while this
    // avoids a legacy-only NVVM lowering failure observed on the target NVIDIA
    // driver when the vector load is inlined into the recursive gather.
    vec4 sourceRadianceDistance = uintBitsToFloat(uvec4(
        ReadStorageWordUniform(bufferIndex, baseWord + 0u),
        ReadStorageWordUniform(bufferIndex, baseWord + 1u),
        ReadStorageWordUniform(bufferIndex, baseWord + 2u),
        ReadStorageWordUniform(bufferIndex, baseWord + 3u)));
    vec3 direction = UnpackSimpleDdgiTransportOctDirection(
        ReadStorageWordUniform(bufferIndex, baseWord + 4u));
    uint packedTransmission = ReadStorageWordUniform(bufferIndex, baseWord + 7u);
    uint encodedSourceRayCount = packedTransmission >> 24u;
    uint cachedSourceRayCount = encodedSourceRayCount == 0u
        ? 256u
        : encodedSourceRayCount;
    if (cachedSourceRayCount != expectedSourceRayCount ||
        any(isnan(sourceRadianceDistance)) ||
        any(isinf(sourceRadianceDistance)) ||
        any(lessThan(sourceRadianceDistance.rgb, vec3(0.0))) ||
        sourceRadianceDistance.w < 0.0 ||
        any(isnan(direction)) || any(isinf(direction)) ||
        dot(direction, direction) <= 1.0e-12)
    {
        return false;
    }

    vec4 surface = unpackUnorm4x8(
        ReadStorageWordUniform(bufferIndex, baseWord + 6u));
    cache.sourceRadiance = sourceRadianceDistance.rgb;
    cache.distance = sourceRadianceDistance.w;
    cache.direction = direction;
    cache.normal = UnpackSimpleDdgiTransportOctDirection(
        ReadStorageWordUniform(bufferIndex, baseWord + 5u));
    cache.diffuseReflectance = surface.rgb;
    cache.transmittedDiffuseReflectance =
        unpackUnorm4x8(packedTransmission).rgb;
    cache.materialOcclusion = surface.a;
    cache.sourceRayCount = expectedSourceRayCount;
    cache.generationAndFlags = generationAndFlags;
    cache.sourceLightingGeneration = expectedSourceLightingGeneration;
    cache.sourceEpoch = expectedSourceEpoch;
    return true;
}

// Packed transport already receives a producer-validated, one-based cache
// address in the update record. Decode the complete solver payload from that
// address instead of repeating sparse-page ownership resolution for every ray.
// The compact format/ABI/codebook/stride/cardinality and uint-overflow checks
// remain centralized in TryResolveSimpleDdgiTransportCacheAddressFromProbeBase.
bool ReadSimpleDdgiPackedTransportRayCacheForSolve(
    uint bufferIndex,
    uint cacheProbeBaseWordPlusOne,
    uint directionProbeIndex,
    uint directionRayIndex,
    SimpleDdgiVolume volume,
    SimpleDdgiParams p,
    uint expectedProbeGeneration,
    uint expectedSourceLightingGeneration,
    uint expectedSourceEpoch,
    uint expectedSourceRayCount,
    out SimpleDdgiTransportRayCache cache)
{
    cache.sourceRadiance = vec3(0.0);
    cache.distance = 0.0;
    cache.direction = vec3(0.0, 1.0, 0.0);
    cache.normal = vec3(0.0, 1.0, 0.0);
    cache.diffuseReflectance = vec3(0.0);
    cache.transmittedDiffuseReflectance = vec3(0.0);
    cache.materialOcclusion = 1.0;
    cache.sourceRayCount = 0u;
    cache.generationAndFlags = 0u;
    cache.sourceLightingGeneration = 0u;
    cache.sourceEpoch = 0u;
    if (bufferIndex == 0u || expectedProbeGeneration == 0u ||
        expectedSourceLightingGeneration == 0u || expectedSourceEpoch == 0u ||
        expectedSourceRayCount == 0u || expectedSourceRayCount > p.raysPerProbe)
    {
        return false;
    }

    uint baseWord;
    uint format;
    if (!TryResolveSimpleDdgiTransportCacheAddressFromProbeBase(
            cacheProbeBaseWordPlusOne,
            directionRayIndex,
            p.raysPerProbe,
            volume.cacheStrideWords,
            volume.cacheLayoutFlags,
            baseWord,
            format) ||
        (format != SIMPLE_DDGI_CACHE_FORMAT_COMPACT_28 &&
            format != SIMPLE_DDGI_CACHE_FORMAT_COMPACT_24))
    {
        return false;
    }

    uint generationWord = baseWord +
        SimpleDdgiStorageExpectedStride(format) - 1u;
    uint generationAndFlags = ReadStorageWordUniform(bufferIndex, generationWord);
    uint cachedDirectionEpoch = (generationAndFlags &
        SIMPLE_DDGI_TRANSPORT_CACHE_DIRECTION_EPOCH_MASK) >>
        SIMPLE_DDGI_TRANSPORT_CACHE_DIRECTION_EPOCH_SHIFT;
    bool epochMatches = cachedDirectionEpoch ==
        SimpleDdgiDirectionEpoch(expectedSourceEpoch);
    if ((generationAndFlags & SIMPLE_DDGI_TRANSPORT_CACHE_VALID_FLAG) == 0u ||
        (generationAndFlags & SIMPLE_DDGI_UPDATE_GENERATION_MASK) !=
            (expectedProbeGeneration & SIMPLE_DDGI_UPDATE_GENERATION_MASK) ||
        ((generationAndFlags & SIMPLE_DDGI_TRANSPORT_CACHE_BACKFACE_FLAG) != 0u &&
            (generationAndFlags & SIMPLE_DDGI_TRANSPORT_CACHE_HIT_FLAG) == 0u) ||
        !epochMatches)
    {
        if (!epochMatches)
            RecordSimpleDdgiDirectionEpochMismatch(p);
        return false;
    }

    uint packedRg = ReadStorageWordUniform(bufferIndex, baseWord);
    uint packedBAndDistance = ReadStorageWordUniform(bufferIndex, baseWord + 1u);
    if (format == SIMPLE_DDGI_CACHE_FORMAT_COMPACT_28 &&
        (packedBAndDistance & 0xffff0000u) != 0u)
    {
        return false;
    }
    vec2 radianceRg = unpackHalf2x16(packedRg);
    vec2 radianceBAndDistance = unpackHalf2x16(packedBAndDistance);
    vec4 sourceRadianceDistance = vec4(
        radianceRg,
        radianceBAndDistance.x,
        format == SIMPLE_DDGI_CACHE_FORMAT_COMPACT_28
            ? uintBitsToFloat(ReadStorageWordUniform(bufferIndex, baseWord + 2u))
            : radianceBAndDistance.y);
    if (any(isnan(sourceRadianceDistance)) ||
        any(isinf(sourceRadianceDistance)) ||
        any(lessThan(sourceRadianceDistance.rgb, vec3(0.0))) ||
        sourceRadianceDistance.w < 0.0)
    {
        return false;
    }

    uint normalWord = format == SIMPLE_DDGI_CACHE_FORMAT_COMPACT_28
        ? baseWord + 3u
        : baseWord + 2u;
    uint packedSurface = ReadStorageWordUniform(bufferIndex, normalWord + 1u);
    uint packedTransmission = ReadStorageWordUniform(bufferIndex, normalWord + 2u);
    if ((packedTransmission & 0xff000000u) != 0u)
        return false;

    cache.sourceRadiance = sourceRadianceDistance.rgb;
    cache.distance = sourceRadianceDistance.w;
    cache.direction = SimpleDdgiReconstructRayDirection(
        directionProbeIndex,
        directionRayIndex,
        expectedSourceEpoch,
        p);
    cache.normal = UnpackSimpleDdgiTransportOctDirection(
        ReadStorageWordUniform(bufferIndex, normalWord));
    vec4 surface = unpackUnorm4x8(packedSurface);
    cache.diffuseReflectance = surface.rgb;
    cache.transmittedDiffuseReflectance =
        unpackUnorm4x8(packedTransmission).rgb;
    cache.materialOcclusion = surface.a;
    cache.sourceRayCount = expectedSourceRayCount;
    cache.generationAndFlags = generationAndFlags;
    cache.sourceLightingGeneration = expectedSourceLightingGeneration;
    cache.sourceEpoch = expectedSourceEpoch;
    return true;
}

bool ReadSimpleDdgiTransportRayCache(
    uint paramsBufferIndex,
    uint bufferIndex,
    uint volumeIndex,
    uint physicalProbeIndex,
    uint directionProbeIndex,
    uint directionRayIndex,
    SimpleDdgiParams p,
    uint diagnosticFrameIndex,
    uint expectedProbeGeneration,
    uint expectedSourceLightingGeneration,
    uint expectedSourceEpoch,
    uint expectedSourceRayCount,
    out SimpleDdgiTransportRayCache cache)
{
    cache.sourceRadiance = vec3(0.0);
    cache.distance = 0.0;
    cache.direction = vec3(0.0, 1.0, 0.0);
    cache.normal = vec3(0.0, 1.0, 0.0);
    cache.diffuseReflectance = vec3(0.0);
    cache.transmittedDiffuseReflectance = vec3(0.0);
    cache.materialOcclusion = 1.0;
    cache.sourceRayCount = 0u;
    cache.generationAndFlags = 0u;
    cache.sourceLightingGeneration = 0u;
    cache.sourceEpoch = 0u;
    if (bufferIndex == 0u || directionRayIndex >= p.raysPerProbe ||
        expectedProbeGeneration == 0u ||
        expectedSourceLightingGeneration == 0u ||
        expectedSourceEpoch == 0u ||
        expectedSourceRayCount == 0u ||
        expectedSourceRayCount > p.raysPerProbe)
        return false;

    uint baseWord;
    uint format;
    uint layoutFlags;
    if (!TryResolveSimpleDdgiTransportCacheAddress(
            paramsBufferIndex,
            volumeIndex,
            physicalProbeIndex,
            directionRayIndex,
            p.raysPerProbe,
            p.sparsePhysicalPageCapacity,
            baseWord,
            format,
            layoutFlags))
    {
        return false;
    }
    uint generationWord = baseWord +
        SimpleDdgiStorageExpectedStride(format) - 1u;
    uint generationAndFlags = ReadStorageWordUniform(bufferIndex, generationWord);
    if ((generationAndFlags & SIMPLE_DDGI_TRANSPORT_CACHE_VALID_FLAG) == 0u ||
        (generationAndFlags & SIMPLE_DDGI_UPDATE_GENERATION_MASK) !=
            (expectedProbeGeneration & SIMPLE_DDGI_UPDATE_GENERATION_MASK) ||
        ((generationAndFlags & SIMPLE_DDGI_TRANSPORT_CACHE_BACKFACE_FLAG) != 0u &&
            (generationAndFlags & SIMPLE_DDGI_TRANSPORT_CACHE_HIT_FLAG) == 0u))
    {
        return false;
    }

#if SIMPLE_DDGI_STORAGE_SHADER_MODE == SIMPLE_DDGI_STORAGE_SHADER_MODE_LEGACY
    vec4 sourceRadianceDistance = ReadStorageVec4Uniform(bufferIndex, baseWord);
#elif SIMPLE_DDGI_STORAGE_SHADER_MODE == SIMPLE_DDGI_STORAGE_SHADER_MODE_PACKED
    vec2 radianceRg = unpackHalf2x16(
        ReadStorageWordUniform(bufferIndex, baseWord));
    uint packedRadianceBAndDistance = ReadStorageWordUniform(
        bufferIndex,
        baseWord + 1u);
    vec2 radianceBAndDistance = unpackHalf2x16(
        packedRadianceBAndDistance);
    vec4 sourceRadianceDistance = vec4(
        radianceRg,
        radianceBAndDistance.x,
        format == SIMPLE_DDGI_CACHE_FORMAT_COMPACT_28
            ? uintBitsToFloat(ReadStorageWordUniform(bufferIndex, baseWord + 2u))
            : radianceBAndDistance.y);
#else
    vec2 radianceRg = format == SIMPLE_DDGI_CACHE_FORMAT_LEGACY_36
        ? vec2(0.0)
        : unpackHalf2x16(ReadStorageWordUniform(bufferIndex, baseWord));
    vec2 radianceBAndDistance =
        format == SIMPLE_DDGI_CACHE_FORMAT_LEGACY_36
            ? vec2(0.0)
            : unpackHalf2x16(ReadStorageWordUniform(
                bufferIndex, baseWord + 1u));
    vec4 sourceRadianceDistance =
        format == SIMPLE_DDGI_CACHE_FORMAT_LEGACY_36
            // Legacy-36 advances by nine words, so its FP32 vec4 is not
            // guaranteed to be four-word aligned after the first entry.
            ? ReadStorageVec4Uniform(bufferIndex, baseWord)
            : vec4(
                radianceRg,
                radianceBAndDistance.x,
                format == SIMPLE_DDGI_CACHE_FORMAT_COMPACT_28
                    ? uintBitsToFloat(ReadStorageWordUniform(
                        bufferIndex, baseWord + 2u))
                    : radianceBAndDistance.y);
#endif
    // The source trace normally clamps these values before writing the cache,
    // but the audit must not turn corrupted storage into a valid black sample.
    // Reject the raw payload before any max/clamp operation can hide a NaN,
    // infinity, or negative radiance component.
    if (any(isnan(sourceRadianceDistance)) ||
        any(isinf(sourceRadianceDistance)) ||
        any(lessThan(sourceRadianceDistance.rgb, vec3(0.0))) ||
        sourceRadianceDistance.w < 0.0)
    {
        return false;
    }
#if SIMPLE_DDGI_STORAGE_SHADER_MODE == SIMPLE_DDGI_STORAGE_SHADER_MODE_PACKED
    if (format == SIMPLE_DDGI_CACHE_FORMAT_COMPACT_28 &&
        (packedRadianceBAndDistance & 0xffff0000u) != 0u)
        return false;
#elif SIMPLE_DDGI_STORAGE_SHADER_MODE == SIMPLE_DDGI_STORAGE_SHADER_MODE_DYNAMIC
    if (format == SIMPLE_DDGI_CACHE_FORMAT_COMPACT_28 &&
        (ReadStorageWordUniform(bufferIndex, baseWord + 1u) & 0xffff0000u) != 0u)
        return false;
#endif
    cache.sourceRadiance = sourceRadianceDistance.rgb;
    cache.distance = sourceRadianceDistance.w;
#if SIMPLE_DDGI_STORAGE_SHADER_MODE == SIMPLE_DDGI_STORAGE_SHADER_MODE_LEGACY
    uint normalWord = baseWord + 5u;
#elif SIMPLE_DDGI_STORAGE_SHADER_MODE == SIMPLE_DDGI_STORAGE_SHADER_MODE_PACKED
    uint normalWord = format == SIMPLE_DDGI_CACHE_FORMAT_COMPACT_28
        ? baseWord + 3u
        : baseWord + 2u;
#else
    uint normalWord = format == SIMPLE_DDGI_CACHE_FORMAT_LEGACY_36
        ? baseWord + 5u
        : format == SIMPLE_DDGI_CACHE_FORMAT_COMPACT_28
            ? baseWord + 3u
            : baseWord + 2u;
#endif
    uint materialWord = normalWord + 1u;
    uint transmissionWord = normalWord + 2u;
    uint packedTransmission = ReadStorageWordUniform(
        bufferIndex,
        transmissionWord);
#if SIMPLE_DDGI_STORAGE_SHADER_MODE == SIMPLE_DDGI_STORAGE_SHADER_MODE_LEGACY
    cache.direction = UnpackSimpleDdgiTransportOctDirection(
        ReadStorageWordUniform(bufferIndex, baseWord + 4u));
#elif SIMPLE_DDGI_STORAGE_SHADER_MODE == SIMPLE_DDGI_STORAGE_SHADER_MODE_PACKED
    cache.direction = SimpleDdgiReconstructRayDirection(
        directionProbeIndex,
        directionRayIndex,
        expectedSourceEpoch,
        p);
#else
    cache.direction = format == SIMPLE_DDGI_CACHE_FORMAT_LEGACY_36
        ? UnpackSimpleDdgiTransportOctDirection(
            ReadStorageWordUniform(bufferIndex, baseWord + 4u))
        : SimpleDdgiReconstructRayDirection(
            directionProbeIndex,
            directionRayIndex,
            expectedSourceEpoch != 0u
                ? expectedSourceEpoch
                : (generationAndFlags &
                    SIMPLE_DDGI_TRANSPORT_CACHE_DIRECTION_EPOCH_MASK) >>
                    SIMPLE_DDGI_TRANSPORT_CACHE_DIRECTION_EPOCH_SHIFT,
            p);
#endif
    cache.normal = UnpackSimpleDdgiTransportOctDirection(
        ReadStorageWordUniform(bufferIndex, normalWord));
    vec4 packedSurface = unpackUnorm4x8(
        ReadStorageWordUniform(bufferIndex, materialWord));
    cache.diffuseReflectance = packedSurface.rgb;
    cache.transmittedDiffuseReflectance =
        unpackUnorm4x8(packedTransmission).rgb;
    cache.materialOcclusion = packedSurface.a;
    cache.generationAndFlags = generationAndFlags;
#if SIMPLE_DDGI_STORAGE_SHADER_MODE == SIMPLE_DDGI_STORAGE_SHADER_MODE_LEGACY
    uint encodedSourceRayCount = packedTransmission >> 24u;
    cache.sourceRayCount = encodedSourceRayCount == 0u
        ? 256u
        : encodedSourceRayCount;
    uint cachedDirectionEpoch = (generationAndFlags &
        SIMPLE_DDGI_TRANSPORT_CACHE_DIRECTION_EPOCH_MASK) >>
        SIMPLE_DDGI_TRANSPORT_CACHE_DIRECTION_EPOCH_SHIFT;
    if (cachedDirectionEpoch != SimpleDdgiDirectionEpoch(expectedSourceEpoch))
    {
        RecordSimpleDdgiDirectionEpochMismatch(p);
        return false;
    }
    cache.sourceLightingGeneration = expectedSourceLightingGeneration;
    cache.sourceEpoch = expectedSourceEpoch;
#elif SIMPLE_DDGI_STORAGE_SHADER_MODE == SIMPLE_DDGI_STORAGE_SHADER_MODE_PACKED
    if ((packedTransmission & 0xff000000u) != 0u)
        return false;
    uint cachedDirectionEpoch = (generationAndFlags &
        SIMPLE_DDGI_TRANSPORT_CACHE_DIRECTION_EPOCH_MASK) >>
        SIMPLE_DDGI_TRANSPORT_CACHE_DIRECTION_EPOCH_SHIFT;
    if (cachedDirectionEpoch != SimpleDdgiDirectionEpoch(expectedSourceEpoch))
    {
        RecordSimpleDdgiDirectionEpochMismatch(p);
        return false;
    }
    cache.sourceRayCount = expectedSourceRayCount;
    cache.sourceLightingGeneration = expectedSourceLightingGeneration;
    cache.sourceEpoch = expectedSourceEpoch;
#else
    if (format == SIMPLE_DDGI_CACHE_FORMAT_LEGACY_36)
    {
        uint encodedSourceRayCount = packedTransmission >> 24u;
        cache.sourceRayCount = encodedSourceRayCount == 0u
            ? 256u
            : encodedSourceRayCount;
        uint cachedDirectionEpoch = (generationAndFlags &
            SIMPLE_DDGI_TRANSPORT_CACHE_DIRECTION_EPOCH_MASK) >>
            SIMPLE_DDGI_TRANSPORT_CACHE_DIRECTION_EPOCH_SHIFT;
        if (expectedSourceEpoch != 0u && cachedDirectionEpoch !=
                SimpleDdgiDirectionEpoch(expectedSourceEpoch))
        {
            RecordSimpleDdgiDirectionEpochMismatch(p);
            return false;
        }
        cache.sourceLightingGeneration = expectedSourceLightingGeneration;
        cache.sourceEpoch = expectedSourceEpoch != 0u
            ? expectedSourceEpoch
            : cachedDirectionEpoch;
    }
    else
    {
        if ((packedTransmission & 0xff000000u) != 0u)
            return false;
        uint cachedDirectionEpoch = (generationAndFlags &
            SIMPLE_DDGI_TRANSPORT_CACHE_DIRECTION_EPOCH_MASK) >>
            SIMPLE_DDGI_TRANSPORT_CACHE_DIRECTION_EPOCH_SHIFT;
        if (expectedSourceEpoch != 0u && cachedDirectionEpoch !=
                SimpleDdgiDirectionEpoch(expectedSourceEpoch))
        {
            RecordSimpleDdgiDirectionEpochMismatch(p);
            return false;
        }
        cache.sourceRayCount = expectedSourceRayCount;
        cache.sourceLightingGeneration = expectedSourceLightingGeneration;
        cache.sourceEpoch = expectedSourceEpoch != 0u
            ? expectedSourceEpoch
            : cachedDirectionEpoch;
    }
#endif
    bool sourceEpochMismatch = expectedSourceEpoch != 0u &&
        cache.sourceEpoch != expectedSourceEpoch;
    if ((expectedSourceRayCount != 0u &&
            cache.sourceRayCount != expectedSourceRayCount) ||
        (expectedSourceLightingGeneration != 0u &&
            cache.sourceLightingGeneration != expectedSourceLightingGeneration) ||
        sourceEpochMismatch)
    {
        if (sourceEpochMismatch)
            RecordSimpleDdgiDirectionEpochMismatch(p);
        return false;
    }
#if SIMPLE_DDGI_DIRECTION_VALIDATION
    if (format == SIMPLE_DDGI_CACHE_FORMAT_LEGACY_36)
    {
        RecordSimpleDdgiDirectionComparison(
            p,
            diagnosticFrameIndex,
            directionProbeIndex,
            directionRayIndex,
            cache.direction,
            SimpleDdgiReconstructRayDirection(
                directionProbeIndex,
                directionRayIndex,
                cache.sourceEpoch,
                p));
    }
#elif SIMPLE_DDGI_STORAGE_SHADER_MODE != SIMPLE_DDGI_STORAGE_SHADER_MODE_PACKED
    if (format == SIMPLE_DDGI_CACHE_FORMAT_LEGACY_36 &&
        (p.flags & SIMPLE_DDGI_FLAG_DIRECTION_VALIDATE) != 0u)
    {
        RecordSimpleDdgiDirectionComparison(
            p,
            diagnosticFrameIndex,
            directionProbeIndex,
            directionRayIndex,
            cache.direction,
            SimpleDdgiReconstructRayDirection(
                directionProbeIndex,
                directionRayIndex,
                cache.sourceEpoch,
            p));
    }
#endif
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

vec4 ReadSimpleDdgiIrradianceTexel(
    uint bufferIndex,
    uint probeIndex,
    uint texelIndex,
    uint texelsPerProbe)
{
    uint word = SimpleDdgiIrradianceAtlasWord(
        probeIndex, texelIndex, texelsPerProbe);
    return vec4(
        unpackHalf2x16(ReadStorageWordUniform(bufferIndex, word)),
        unpackHalf2x16(ReadStorageWordUniform(bufferIndex, word + 1u)));
}

vec4 ReadSimpleDdgiIrradianceTexelAtBase(
    uint bufferIndex,
    uint probeBaseWord,
    uint texelIndex)
{
    uint word = probeBaseWord + texelIndex * 2u;
    return vec4(
        unpackHalf2x16(ReadStorageWordUniform(bufferIndex, word)),
        unpackHalf2x16(ReadStorageWordUniform(bufferIndex, word + 1u)));
}

void WriteSimpleDdgiIrradianceTexel(
    uint bufferIndex,
    uint probeIndex,
    uint texelIndex,
    uint texelsPerProbe,
    vec4 value)
{
    value = clamp(value, vec4(0.0), vec4(65504.0));
    uint word = SimpleDdgiIrradianceAtlasWord(
        probeIndex, texelIndex, texelsPerProbe);
    WriteStorageWordUniform(bufferIndex, word, packHalf2x16(value.xy));
    WriteStorageWordUniform(bufferIndex, word + 1u, packHalf2x16(value.zw));
}

vec4 ReadSimpleDdgiIrradianceTexelAtOffset(
    uint bufferIndex,
    uint baseOffsetWords,
    uint probeIndex,
    uint texelIndex,
    uint texelsPerProbe)
{
    uint word = baseOffsetWords + SimpleDdgiIrradianceAtlasWord(
        probeIndex, texelIndex, texelsPerProbe);
    return vec4(
        unpackHalf2x16(ReadStorageWordUniform(bufferIndex, word)),
        unpackHalf2x16(ReadStorageWordUniform(bufferIndex, word + 1u)));
}

void WriteSimpleDdgiIrradianceTexelAtOffset(
    uint bufferIndex,
    uint baseOffsetWords,
    uint probeIndex,
    uint texelIndex,
    uint texelsPerProbe,
    vec4 value)
{
    value = clamp(value, vec4(0.0), vec4(65504.0));
    uint word = baseOffsetWords + SimpleDdgiIrradianceAtlasWord(
        probeIndex, texelIndex, texelsPerProbe);
    WriteStorageWordUniform(bufferIndex, word, packHalf2x16(value.xy));
    WriteStorageWordUniform(bufferIndex, word + 1u, packHalf2x16(value.zw));
}

vec2 ReadSimpleDdgiVisibilityMoments(
    uint bufferIndex,
    uint probeIndex,
    uint texelIndex,
    uint texelsPerProbe)
{
    return unpackHalf2x16(ReadStorageWordUniform(
        bufferIndex,
        SimpleDdgiVisibilityAtlasWord(
            probeIndex, texelIndex, texelsPerProbe)));
}

vec2 ReadSimpleDdgiVisibilityMomentsAtBase(
    uint bufferIndex,
    uint probeBaseWord,
    uint texelIndex)
{
    return unpackHalf2x16(ReadStorageWordUniform(
        bufferIndex,
        probeBaseWord + texelIndex));
}

void WriteSimpleDdgiVisibilityMoments(
    uint bufferIndex,
    uint probeIndex,
    uint texelIndex,
    uint texelsPerProbe,
    vec2 moments)
{
    WriteStorageWordUniform(
        bufferIndex,
        SimpleDdgiVisibilityAtlasWord(
            probeIndex, texelIndex, texelsPerProbe),
        packHalf2x16(clamp(moments, vec2(0.0), vec2(65504.0))));
}

vec2 ReadSimpleDdgiVisibilityMomentsAtOffset(
    uint bufferIndex,
    uint baseOffsetWords,
    uint probeIndex,
    uint texelIndex,
    uint texelsPerProbe)
{
    return unpackHalf2x16(ReadStorageWordUniform(
        bufferIndex,
        baseOffsetWords + SimpleDdgiVisibilityAtlasWord(
            probeIndex, texelIndex, texelsPerProbe)));
}

void WriteSimpleDdgiVisibilityMomentsAtOffset(
    uint bufferIndex,
    uint baseOffsetWords,
    uint probeIndex,
    uint texelIndex,
    uint texelsPerProbe,
    vec2 moments)
{
    WriteStorageWordUniform(
        bufferIndex,
        baseOffsetWords + SimpleDdgiVisibilityAtlasWord(
            probeIndex, texelIndex, texelsPerProbe),
        packHalf2x16(clamp(moments, vec2(0.0), vec2(65504.0))));
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

// One canonical logical slot is resolved per accepted corner and reused for
// both atlases. Precomputing the SSBO bases also removes eight repeated
// probe-stride multiplies from each bilinear pair.
struct SimpleDdgiAtlasAddress
{
    uint irradianceBaseWord;
    uint visibilityBaseWord;
    uint sampledGroupIndex;
    uint sampledLayerIndex;
    uint sampledStatusFlags;
};

const uint SIMPLE_DDGI_ATLAS_ADDRESS_MAPPING_VALID_BIT = 1u << 4u;
const uint SIMPLE_DDGI_ATLAS_ADDRESS_LAYER_BASE_DECLARED_BIT = 1u << 5u;

bool TryBuildSimpleDdgiAtlasAddress(
    SimpleDdgiParams p,
    SimpleDdgiVolume volume,
    SimpleDdgiVolumePaging paging,
    uint atlasProbeAddress,
    out SimpleDdgiAtlasAddress address)
{
    address.irradianceBaseWord = 0u;
    address.visibilityBaseWord = 0u;
    address.sampledGroupIndex = 0u;
    address.sampledLayerIndex = 0u;
    // Preserve the raw two-part declaration even when mapping validation fails.
    address.sampledStatusFlags = volume.cacheLayoutFlags &
        (SIMPLE_DDGI_CACHE_IRRADIANCE_MIRROR_BIT |
            SIMPLE_DDGI_CACHE_VISIBILITY_MIRROR_BIT);
    if (volume.compactMirrorFirstLayerPlusOne != 0u)
    {
        address.sampledStatusFlags |=
            SIMPLE_DDGI_ATLAS_ADDRESS_LAYER_BASE_DECLARED_BIT;
    }
    if (atlasProbeAddress == SIMPLE_DDGI_RECEIVER_INVALID_ATLAS_ADDRESS ||
        atlasProbeAddress >= p.physicalProbeCapacity ||
        p.irradianceTexels == 0u || p.visibilityTexels == 0u ||
        // floor(sqrt(UINT_MAX / two packed words per irradiance texel)). This
        // validates the square before evaluating it, so corrupt parameters
        // cannot wrap into a plausible nonzero stride.
        p.irradianceTexels > 46340u || p.visibilityTexels > 46340u)
    {
        return false;
    }

    uint irradianceWordsPerProbe =
        p.irradianceTexels * p.irradianceTexels * 2u;
    uint visibilityWordsPerProbe =
        p.visibilityTexels * p.visibilityTexels;
    if (irradianceWordsPerProbe == 0u || visibilityWordsPerProbe == 0u ||
        atlasProbeAddress >
            (0xffffffffu - (irradianceWordsPerProbe - 1u)) /
                irradianceWordsPerProbe ||
        atlasProbeAddress >
            (0xffffffffu - (visibilityWordsPerProbe - 1u)) /
                visibilityWordsPerProbe)
    {
        return false;
    }

    address.irradianceBaseWord = atlasProbeAddress * irradianceWordsPerProbe;
    address.visibilityBaseWord = atlasProbeAddress * visibilityWordsPerProbe;
    uint mirrorPayloadBits = address.sampledStatusFlags &
        (SIMPLE_DDGI_CACHE_IRRADIANCE_MIRROR_BIT |
            SIMPLE_DDGI_CACHE_VISIBILITY_MIRROR_BIT);
    bool completeMirrorPayloadPair = mirrorPayloadBits ==
        (SIMPLE_DDGI_CACHE_IRRADIANCE_MIRROR_BIT |
            SIMPLE_DDGI_CACHE_VISIBILITY_MIRROR_BIT);
    if (volume.compactMirrorFirstLayerPlusOne != 0u &&
        completeMirrorPayloadPair &&
        p.sampledAtlasLayersPerTexture != 0u &&
        p.sampledAtlasTextureGroupCount != 0u &&
        p.sampledAtlasProbeCapacity != 0u)
    {
        uint physicalFirstProbe = paging.residencyMode ==
                SIMPLE_DDGI_RESIDENCY_MODE_SPARSE_NEAR_RING
            ? paging.sparsePoolFirstProbe
            : paging.densePhysicalFirstProbe;
        uint physicalProbeCount = paging.residencyMode ==
                SIMPLE_DDGI_RESIDENCY_MODE_SPARSE_NEAR_RING
            ? p.sparsePhysicalPageCapacity * 8u
            : volume.gridCount.x * volume.gridCount.y * volume.gridCount.z;
        if (atlasProbeAddress >= physicalFirstProbe)
        {
            uint localProbe = atlasProbeAddress - physicalFirstProbe;
            uint compactBase = volume.compactMirrorFirstLayerPlusOne - 1u;
            if (localProbe < physicalProbeCount &&
                localProbe <= 0xffffffffu - compactBase)
            {
                uint compactLayer = compactBase + localProbe;
                // The CPU publishes the exact provisioned layer count. Checking
                // it here is both stricter and cheaper than a dynamically indexed
                // textureSize query, and avoids a known NVIDIA NVVM failure when
                // that query is inlined into the full receiver gather.
                if (compactLayer < p.sampledAtlasProbeCapacity)
                {
                    address.sampledGroupIndex =
                        compactLayer / p.sampledAtlasLayersPerTexture;
                    address.sampledLayerIndex = compactLayer -
                        address.sampledGroupIndex * p.sampledAtlasLayersPerTexture;
                    if (address.sampledGroupIndex < p.sampledAtlasTextureGroupCount &&
                        address.sampledGroupIndex <
                            uint(SIMPLE_DDGI_SAMPLED_ATLAS_TEXTURE_GROUP_COUNT))
                    {
                        address.sampledStatusFlags |=
                            SIMPLE_DDGI_ATLAS_ADDRESS_MAPPING_VALID_BIT;
                    }
                }
            }
        }
    }
    return true;
}

bool SimpleDdgiCanSampleAtlasImageAtAddress(
    SimpleDdgiParams p,
    uint requiredPayloadBit,
    SimpleDdgiAtlasAddress address)
{
    return SIMPLE_DDGI_DISABLE_SAMPLED_ATLAS == 0 &&
        p.sampledAtlasEnabled != 0u &&
        p.sampledAtlasLayersPerTexture != 0u &&
        p.sampledAtlasTextureGroupCount != 0u &&
        (address.sampledStatusFlags &
            SIMPLE_DDGI_ATLAS_ADDRESS_MAPPING_VALID_BIT) != 0u &&
        (address.sampledStatusFlags & requiredPayloadBit) != 0u &&
        address.sampledGroupIndex < p.sampledAtlasTextureGroupCount &&
        address.sampledGroupIndex < uint(SIMPLE_DDGI_SAMPLED_ATLAS_TEXTURE_GROUP_COUNT);
}

vec4 SampleSimpleDdgiAtlasImageAtAddress(
    int textureBaseIndex,
    SimpleDdgiAtlasAddress address,
    vec2 encodedDirection)
{
    int textureIndex = textureBaseIndex + int(address.sampledGroupIndex);
    return texture(
        BindlessArrayTextures[nonuniformEXT(textureIndex)],
        vec3(
            clamp(encodedDirection, vec2(0.0), vec2(1.0)),
            float(address.sampledLayerIndex)));
}

vec4 SampleSimpleDdgiIrradianceBilinearAtAddress(
    uint bufferIndex,
    SimpleDdgiAtlasAddress address,
    vec3 direction,
    uint texelsPerProbe,
    SimpleDdgiParams p)
{
    vec2 encodedDirection = SimpleDdgiOctEncode(direction);
    vec2 texelUv = encodedDirection * float(texelsPerProbe) - vec2(0.5);
    ivec2 base = ivec2(floor(texelUv));
    vec2 f = fract(texelUv);
    bool interior = all(greaterThanEqual(base, ivec2(0))) &&
        all(lessThan(base + ivec2(1), ivec2(int(texelsPerProbe))));
    bool imageHit = interior &&
        SimpleDdgiCanSampleAtlasImageAtAddress(
            p,
            SIMPLE_DDGI_CACHE_IRRADIANCE_MIRROR_BIT,
            address);
    RecordSimpleDdgiMirrorSamplingPath(
        p,
        address.irradianceBaseWord ^ 0x49525241u,
        interior,
        imageHit,
        SimpleDdgiMirrorPayloadOrMappingDeclared(
            address.sampledStatusFlags &
                SIMPLE_DDGI_ATLAS_ADDRESS_LAYER_BASE_DECLARED_BIT,
            address.sampledStatusFlags,
            SIMPLE_DDGI_CACHE_IRRADIANCE_MIRROR_BIT));
    if (imageHit)
    {
        return SampleSimpleDdgiAtlasImageAtAddress(
            SIMPLE_DDGI_SAMPLED_IRRADIANCE_TEXTURE_BASE_INDEX,
            address,
            encodedDirection);
    }

    vec4 s00 = ReadSimpleDdgiIrradianceTexelAtBase(
        bufferIndex, address.irradianceBaseWord,
        SimpleDdgiMirrorOctTexelIndex(base, texelsPerProbe));
    vec4 s10 = ReadSimpleDdgiIrradianceTexelAtBase(
        bufferIndex, address.irradianceBaseWord,
        SimpleDdgiMirrorOctTexelIndex(base + ivec2(1, 0), texelsPerProbe));
    vec4 s01 = ReadSimpleDdgiIrradianceTexelAtBase(
        bufferIndex, address.irradianceBaseWord,
        SimpleDdgiMirrorOctTexelIndex(base + ivec2(0, 1), texelsPerProbe));
    vec4 s11 = ReadSimpleDdgiIrradianceTexelAtBase(
        bufferIndex, address.irradianceBaseWord,
        SimpleDdgiMirrorOctTexelIndex(base + ivec2(1, 1), texelsPerProbe));
    return mix(mix(s00, s10, f.x), mix(s01, s11, f.x), f.y);
}

vec2 SampleSimpleDdgiVisibilityBilinearAtAddress(
    uint bufferIndex,
    SimpleDdgiAtlasAddress address,
    vec3 direction,
    uint texelsPerProbe,
    SimpleDdgiParams p)
{
    vec2 encodedDirection = SimpleDdgiOctEncode(direction);
    vec2 texelUv = encodedDirection * float(texelsPerProbe) - vec2(0.5);
    ivec2 base = ivec2(floor(texelUv));
    vec2 f = fract(texelUv);
    bool interior = all(greaterThanEqual(base, ivec2(0))) &&
        all(lessThan(base + ivec2(1), ivec2(int(texelsPerProbe))));
    bool imageHit = interior &&
        SimpleDdgiCanSampleAtlasImageAtAddress(
            p,
            SIMPLE_DDGI_CACHE_VISIBILITY_MIRROR_BIT,
            address);
    RecordSimpleDdgiMirrorSamplingPath(
        p,
        address.visibilityBaseWord ^ 0x56495349u,
        interior,
        imageHit,
        SimpleDdgiMirrorPayloadOrMappingDeclared(
            address.sampledStatusFlags &
                SIMPLE_DDGI_ATLAS_ADDRESS_LAYER_BASE_DECLARED_BIT,
            address.sampledStatusFlags,
            SIMPLE_DDGI_CACHE_VISIBILITY_MIRROR_BIT));
    if (imageHit)
    {
        return SampleSimpleDdgiAtlasImageAtAddress(
            SIMPLE_DDGI_SAMPLED_VISIBILITY_TEXTURE_BASE_INDEX,
            address,
            encodedDirection).xy;
    }

    vec2 s00 = ReadSimpleDdgiVisibilityMomentsAtBase(
        bufferIndex, address.visibilityBaseWord,
        SimpleDdgiMirrorOctTexelIndex(base, texelsPerProbe));
    vec2 s10 = ReadSimpleDdgiVisibilityMomentsAtBase(
        bufferIndex, address.visibilityBaseWord,
        SimpleDdgiMirrorOctTexelIndex(base + ivec2(1, 0), texelsPerProbe));
    vec2 s01 = ReadSimpleDdgiVisibilityMomentsAtBase(
        bufferIndex, address.visibilityBaseWord,
        SimpleDdgiMirrorOctTexelIndex(base + ivec2(0, 1), texelsPerProbe));
    vec2 s11 = ReadSimpleDdgiVisibilityMomentsAtBase(
        bufferIndex, address.visibilityBaseWord,
        SimpleDdgiMirrorOctTexelIndex(base + ivec2(1, 1), texelsPerProbe));
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

vec2 SimpleDdgiSampleBiasMagnitudes(
    SimpleDdgiParams p,
    float volumeSpacing)
{
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
    return vec2(normalBias, viewBias);
}

vec3 SimpleDdgiBiasedSamplePosition(vec3 worldPos, vec3 normal, vec3 viewDir, SimpleDdgiParams p, float volumeSpacing)
{
    vec3 safeNormal = length(normal) > 0.00001 ? normalize(normal) : vec3(0.0, 1.0, 0.0);
    vec3 safeView = length(viewDir) > 0.00001 ? normalize(viewDir) : safeNormal;
    vec2 bias = SimpleDdgiSampleBiasMagnitudes(p, volumeSpacing);
    return worldPos + safeNormal * bias.x + safeView * bias.y;
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
#if NJULF_DDGI_DETAILED_COUNTERS
    // Direct + emissive + sky source cache evaluated independently of recursive
    // bounce, using the same probe/cascade weights as the receiver gather.
    vec3 sourceCacheIrradiance;
    // Weighted with the same directional masses that compose irradiance. This
    // lets the cascade-selection debug view show every volume that contributed,
    // rather than only the finest volume selected before fallback blending.
    vec3 contributingVolumeColor;
#endif
    // Fraction of spatial interpolation mass backed by state-valid probe data.
    // Probe-to-receiver visibility is deliberately excluded: it selects which
    // valid probes represent irradiance, but it does not make their atlas data
    // unavailable.
    float validSupport;
    float directionalSupport;
    float spatialCoverage;
    float transportVisibility;
    float ownership;
    uint nonResidentProbeCount;
#if NJULF_SIMPLE_DDGI_GATHER_ATTRIBUTION
    uint selectedVolume;
    uint secondaryVolume;
    float primaryContributionWeight;
    float secondaryContributionWeight;
#endif
#if NJULF_DDGI_DETAILED_COUNTERS
    float selectedSpacing;
    float transitionWeight;
    float secondVolumeUsed;
    uint validProbeCount;
    uint combinedRejectionMask;
    uint firstRejectionReason;
    uint rejectedProbeCount;
#endif
};

SimpleDdgiGatherResult EmptySimpleDdgiGatherResult()
{
    SimpleDdgiGatherResult result;
    result.irradiance = vec3(0.0);
#if NJULF_DDGI_DETAILED_COUNTERS
    result.sourceCacheIrradiance = vec3(0.0);
    result.contributingVolumeColor = vec3(0.0);
#endif
    result.validSupport = 0.0;
    result.directionalSupport = 0.0;
    result.spatialCoverage = 0.0;
    result.transportVisibility = 0.0;
    result.ownership = 0.0;
    result.nonResidentProbeCount = 0u;
#if NJULF_SIMPLE_DDGI_GATHER_ATTRIBUTION
    result.selectedVolume = 0u;
    result.secondaryVolume = SIMPLE_DDGI_INVALID_VOLUME_INDEX;
    result.primaryContributionWeight = 0.0;
    result.secondaryContributionWeight = 0.0;
#endif
#if NJULF_DDGI_DETAILED_COUNTERS
    result.selectedSpacing = 0.0;
    result.transitionWeight = 1.0;
    result.secondVolumeUsed = 0.0;
    result.validProbeCount = 0u;
    result.combinedRejectionMask = 0u;
    result.firstRejectionReason = SIMPLE_DDGI_GATHER_REJECTION_REASON_COUNT;
    result.rejectedProbeCount = 0u;
#endif
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

uint SimpleDdgiProbeGatherRejectionMask(SimpleDdgiProbeState state, vec4 irradiance);
bool SimpleDdgiProbeStateSupportsGather(SimpleDdgiProbeState state);

bool SimpleDdgiProbeSupportsGather(SimpleDdgiProbeState state, vec4 irradiance)
{
    return SimpleDdgiProbeStateSupportsGather(state) &&
        irradiance.w > 0.5;
}

bool SimpleDdgiProbeStateSupportsGather(SimpleDdgiProbeState state)
{
    return (state.flags & (
            SIMPLE_DDGI_PROBE_FLAG_FRESH |
            SIMPLE_DDGI_PROBE_FLAG_SCROLL_EXPOSED |
            SIMPLE_DDGI_PROBE_FLAG_RELOCATION_PENDING |
            SIMPLE_DDGI_PROBE_FLAG_INACTIVE)) == 0u &&
        (state.flags & SIMPLE_DDGI_PROBE_FLAG_VISIBILITY_VALID) != 0u &&
        state.classification != SIMPLE_DDGI_CLASSIFICATION_INACTIVE &&
        state.activeWeight > SIMPLE_DDGI_RECEIVER_ACTIVE_WEIGHT_THRESHOLD;
}

bool SimpleDdgiReceiverProbeSupportsGather(SimpleDdgiReceiverProbe probe)
{
    return (probe.flags & SIMPLE_DDGI_RECEIVER_FLAG_PUBLISHED_COHERENT) != 0u &&
        (probe.flags & SIMPLE_DDGI_RECEIVER_STATE_REJECTION_FLAGS) == 0u &&
        probe.activeWeight > SIMPLE_DDGI_RECEIVER_ACTIVE_WEIGHT_THRESHOLD &&
        probe.atlasProbeAddress != SIMPLE_DDGI_RECEIVER_INVALID_ATLAS_ADDRESS;
}

bool SimpleDdgiAtlasSupportsGather(vec4 irradiance)
{
    return irradiance.w > 0.5;
}

// Returns the reason mask as well as the legacy Boolean decision. Keeping this
// pure makes it usable by diagnostic builds without adding work to production
// gather paths.
uint SimpleDdgiProbeStateRejectionMask(SimpleDdgiProbeState state)
{
    uint mask = 0u;
    if ((state.flags & SIMPLE_DDGI_PROBE_FLAG_FRESH) != 0u) mask |= SIMPLE_DDGI_GATHER_REJECT_FRESH;
    if ((state.flags & SIMPLE_DDGI_PROBE_FLAG_SCROLL_EXPOSED) != 0u) mask |= SIMPLE_DDGI_GATHER_REJECT_SCROLL_EXPOSED;
    if ((state.flags & SIMPLE_DDGI_PROBE_FLAG_RELOCATION_PENDING) != 0u) mask |= SIMPLE_DDGI_GATHER_REJECT_RELOCATION_PENDING;
    if ((state.flags & SIMPLE_DDGI_PROBE_FLAG_INACTIVE) != 0u) mask |= SIMPLE_DDGI_GATHER_REJECT_INACTIVE_FLAG;
    if ((state.flags & SIMPLE_DDGI_PROBE_FLAG_VISIBILITY_VALID) == 0u) mask |= SIMPLE_DDGI_GATHER_REJECT_INVALID_VISIBILITY;
    if (state.classification == SIMPLE_DDGI_CLASSIFICATION_INACTIVE) mask |= SIMPLE_DDGI_GATHER_REJECT_INACTIVE_CLASSIFICATION;
    if (state.activeWeight <= SIMPLE_DDGI_RECEIVER_ACTIVE_WEIGHT_THRESHOLD) mask |= SIMPLE_DDGI_GATHER_REJECT_ZERO_ACTIVE_WEIGHT;
    return mask;
}

uint SimpleDdgiReceiverProbeStateRejectionMask(SimpleDdgiReceiverProbe probe)
{
    uint mask = 0u;
    if ((probe.flags & SIMPLE_DDGI_RECEIVER_FLAG_PUBLISHED_COHERENT) == 0u)
        mask |= SIMPLE_DDGI_GATHER_REJECT_FRESH;
    if ((probe.flags & SIMPLE_DDGI_RECEIVER_FLAG_FRESH) != 0u)
        mask |= SIMPLE_DDGI_GATHER_REJECT_FRESH;
    if ((probe.flags & SIMPLE_DDGI_RECEIVER_FLAG_SCROLL_EXPOSED) != 0u)
        mask |= SIMPLE_DDGI_GATHER_REJECT_SCROLL_EXPOSED;
    if ((probe.flags & SIMPLE_DDGI_RECEIVER_FLAG_RELOCATION_PENDING) != 0u)
        mask |= SIMPLE_DDGI_GATHER_REJECT_RELOCATION_PENDING;
    if ((probe.flags & SIMPLE_DDGI_RECEIVER_FLAG_INACTIVE) != 0u)
        mask |= SIMPLE_DDGI_GATHER_REJECT_INACTIVE_FLAG;
    if ((probe.flags & SIMPLE_DDGI_RECEIVER_FLAG_INACTIVE_CLASSIFICATION) != 0u)
        mask |= SIMPLE_DDGI_GATHER_REJECT_INACTIVE_CLASSIFICATION;
    if (probe.activeWeight <= SIMPLE_DDGI_RECEIVER_ACTIVE_WEIGHT_THRESHOLD)
        mask |= SIMPLE_DDGI_GATHER_REJECT_ZERO_ACTIVE_WEIGHT;
    if (probe.atlasProbeAddress == SIMPLE_DDGI_RECEIVER_INVALID_ATLAS_ADDRESS)
        mask |= SIMPLE_DDGI_GATHER_REJECT_INVALID_IRRADIANCE |
            SIMPLE_DDGI_GATHER_REJECT_INVALID_VISIBILITY;
    return mask;
}

uint SimpleDdgiAtlasRejectionMask(vec4 irradiance)
{
    uint mask = 0u;
    if (irradiance.w <= 0.5) mask |= SIMPLE_DDGI_GATHER_REJECT_INVALID_IRRADIANCE;
    return mask;
}

uint SimpleDdgiProbeGatherRejectionMask(SimpleDdgiProbeState state, vec4 irradiance)
{
    return SimpleDdgiProbeStateRejectionMask(state) |
        SimpleDdgiAtlasRejectionMask(irradiance);
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
    uint volumeIndex,
    uint probeIndex,
    uint physicalProbeIndex,
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
                uint(SIMPLE_DDGI_PARAMS_BUFFER_INDEX),
                p.transportSourceCacheBufferIndex,
                volumeIndex,
                physicalProbeIndex,
                probeIndex,
                rayIndex,
                p,
                SimpleDdgiStorageDiagnosticFrame(p),
                probeGeneration,
                0u,
                0u,
                0u,
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
#if NJULF_SIMPLE_DDGI_GATHER_ATTRIBUTION
    result.selectedVolume = volumeIndex;
    result.secondaryVolume = SIMPLE_DDGI_INVALID_VOLUME_INDEX;
#endif
#if NJULF_DDGI_DETAILED_COUNTERS
    result.selectedSpacing = volume.spacing;
#endif
    vec3 grid = (biasedWorldPos - volume.origin) / volume.spacing;
    vec3 baseF = floor(grid);
    // Keep the interpolation affine inside each cell. Applying smoothstep to
    // the grid fraction creates probe-centred plateaus followed by a 1.5x
    // steeper transition at the cell midpoint, which exposes small per-probe
    // visibility differences as a repeating light pattern on planar surfaces.
    vec3 fracV = clamp(grid - baseF, vec3(0.0), vec3(1.0));
    ivec3 base = ivec3(baseF);
    vec3 accumulated = vec3(0.0);
#if NJULF_DDGI_DETAILED_COUNTERS
    vec3 sourceCacheAccumulated = vec3(0.0);
#endif
    float availableMass = 0.0;
    float validMass = 0.0;
    float directionalMass = 0.0;
    float visibleMass = 0.0;
#if NJULF_DDGI_DETAILED_COUNTERS
    uint validProbeCount = 0u;
#endif
    SimpleDdgiVolumePaging paging = ReadSimpleDdgiVolumePaging(
        uint(SIMPLE_DDGI_PARAMS_BUFFER_INDEX),
        volumeIndex);
#if !SIMPLE_DDGI_GATHER_USES_COMPUTE_STATE && SIMPLE_DDGI_OPAQUE_GATHER_ORACLE != 0
    uint opaqueDemandEpoch = SimpleDdgiDemandEpochForFrame(
        p.frameIndex == 0xffffffffu
            ? 1u
            : p.frameIndex + 1u);
    vec3 opaqueDemandVolumeCenter = volume.origin +
        0.5 * volume.spacing * vec3(max(
            volume.gridCount,
            uvec3(1u)) - uvec3(1u));
    uint opaqueDemandDistanceBucket = uint(clamp(
        length(biasedWorldPos - opaqueDemandVolumeCenter) /
            max(volume.spacing, 0.0001),
        0.0,
        255.0));
#endif

    for (uint z = 0u; z < 2u; z++)
    for (uint y = 0u; y < 2u; y++)
    for (uint x = 0u; x < 2u; x++)
    {
        ivec3 c = base + ivec3(int(x), int(y), int(z));
        if (any(lessThan(c, ivec3(0))) || any(greaterThanEqual(c, ivec3(volume.gridCount))))
        {
#if NJULF_DDGI_DETAILED_COUNTERS
            result.combinedRejectionMask |= SIMPLE_DDGI_GATHER_REJECT_OUTSIDE_DOMAIN;
            if (result.firstRejectionReason >= SIMPLE_DDGI_GATHER_REJECTION_REASON_COUNT)
                result.firstRejectionReason = 8u;
            result.rejectedProbeCount++;
            RecordSimpleDdgiGatherRejectionMask(
                p,
                gatherRole,
                SIMPLE_DDGI_GATHER_REJECT_OUTSIDE_DOMAIN);
#endif
            continue;
        }

        vec3 w3 = mix(1.0 - fracV, fracV, vec3(x, y, z));
        float trilinear = w3.x * w3.y * w3.z;
        result.spatialCoverage += trilinear;

        SimpleDdgiProbeAddress probeAddress =
#if SIMPLE_DDGI_GATHER_USES_COMPUTE_STATE
            ResolveSimpleDdgiProbeAddress(
#else
            ResolveSimpleDdgiReceiverProbeAddress(
#endif
            p,
            volume,
            paging,
            uvec3(c));
        uint probeIndex = probeAddress.virtualProbeIndex;
#if !SIMPLE_DDGI_GATHER_USES_COMPUTE_STATE
        uint receiverDemandEpoch = SimpleDdgiDemandEpochForFrame(
            p.frameIndex == 0xffffffffu
                ? 1u
                : p.frameIndex +
                    SIMPLE_DDGI_RECEIVER_DEMAND_FRAME_OFFSET);
#if SIMPLE_DDGI_OPAQUE_GATHER_ORACLE != 0
        if (SIMPLE_DDGI_RECEIVER_DEMAND_SAMPLE &&
            probeAddress.virtualPageIndex != 0xffffffffu)
        {
            SimpleDdgiStampOpaqueGatherDemand(
                p,
                probeAddress.virtualPageIndex,
                opaqueDemandEpoch,
                opaqueDemandDistanceBucket);
        }
#endif
        // Shadow keeps its exact opaque oracle separate. Non-depth receivers
        // retain resident pages here; depth-visible opaque receivers defer to
        // the compact publication-miss branch below. Keeping one static call
        // site per shader variant also prevents duplicated atomic machinery.
#if SIMPLE_DDGI_RECEIVER_TOUCHES_RESIDENT != 0
        if (SIMPLE_DDGI_RECEIVER_DEMAND_SAMPLE &&
            probeAddress.virtualPageIndex != 0xffffffffu &&
            (SIMPLE_DDGI_OPAQUE_GATHER_ORACLE == 0 ||
                paging.residencyMode != SIMPLE_DDGI_RESIDENCY_MODE_SHADOW) &&
            (paging.residencyMode == SIMPLE_DDGI_RESIDENCY_MODE_SHADOW ||
                probeAddress.resident && probeAddress.published))
        {
            SimpleDdgiRecordReceiverPageDemand(
                p,
                probeAddress.virtualPageIndex,
                receiverDemandEpoch);
        }
#endif
#endif
#if SIMPLE_DDGI_GATHER_USES_COMPUTE_STATE
        if (!probeAddress.resident || !probeAddress.published)
        {
            result.nonResidentProbeCount++;
#if NJULF_DDGI_DETAILED_COUNTERS
            result.combinedRejectionMask |=
                SIMPLE_DDGI_GATHER_REJECT_NON_RESIDENT;
            if (result.firstRejectionReason >=
                SIMPLE_DDGI_GATHER_REJECTION_REASON_COUNT)
            {
                result.firstRejectionReason = 9u;
            }
            result.rejectedProbeCount++;
            RecordSimpleDdgiGatherRejectionMask(
                p,
                gatherRole,
                SIMPLE_DDGI_GATHER_REJECT_NON_RESIDENT);
#endif
            continue;
        }
#endif
        vec3 relocation;
        float activeWeight;
        uint atlasProbeAddress = probeAddress.physicalProbeIndex;
#if NJULF_DDGI_DETAILED_COUNTERS
        uint rejectionMask;
#endif
        bool stateSupported;
#if SIMPLE_DDGI_GATHER_USES_COMPUTE_STATE
        SimpleDdgiProbeState state = ReadSimpleDdgiProbeState(
            uint(SIMPLE_DDGI_PROBE_STATE_BUFFER_INDEX),
            probeIndex);
        relocation = state.relocation;
        activeWeight = state.activeWeight;
#if NJULF_DDGI_DETAILED_COUNTERS
        rejectionMask = SimpleDdgiProbeStateRejectionMask(state);
        stateSupported = rejectionMask == 0u;
#else
        stateSupported = SimpleDdgiProbeStateSupportsGather(state);
#endif
#else
        SimpleDdgiReceiverProbe receiverProbe = ReadSimpleDdgiReceiverProbe(
            uint(SIMPLE_DDGI_RECEIVER_PROBE_BUFFER_INDEX),
            probeIndex,
            volume.spacing);
        relocation = receiverProbe.relocation;
        activeWeight = receiverProbe.activeWeight;
        atlasProbeAddress = receiverProbe.atlasProbeAddress;
        if (atlasProbeAddress ==
                SIMPLE_DDGI_RECEIVER_INVALID_ATLAS_ADDRESS ||
            atlasProbeAddress >= p.physicalProbeCapacity)
        {
#if SIMPLE_DDGI_RECEIVER_TOUCHES_RESIDENT == 0
            // Depth-visible opaque receivers avoid common-case residency
            // atomics. A compact publication miss is the authoritative point
            // at which supplemental feedback is required.
            if (SIMPLE_DDGI_RECEIVER_DEMAND_SAMPLE &&
                probeAddress.virtualPageIndex != 0xffffffffu &&
                paging.residencyMode != SIMPLE_DDGI_RESIDENCY_MODE_SHADOW)
            {
                SimpleDdgiRecordReceiverPageDemand(
                    p,
                    probeAddress.virtualPageIndex,
                    receiverDemandEpoch);
            }
#endif
            result.nonResidentProbeCount++;
#if NJULF_DDGI_DETAILED_COUNTERS
            result.combinedRejectionMask |=
                SIMPLE_DDGI_GATHER_REJECT_NON_RESIDENT;
            if (result.firstRejectionReason >=
                SIMPLE_DDGI_GATHER_REJECTION_REASON_COUNT)
            {
                result.firstRejectionReason = 9u;
            }
            result.rejectedProbeCount++;
            RecordSimpleDdgiGatherRejectionMask(
                p,
                gatherRole,
                SIMPLE_DDGI_GATHER_REJECT_NON_RESIDENT);
#endif
            continue;
        }
#if NJULF_DDGI_DETAILED_COUNTERS
        rejectionMask =
            SimpleDdgiReceiverProbeStateRejectionMask(receiverProbe);
        stateSupported = rejectionMask == 0u;
#else
        stateSupported = SimpleDdgiReceiverProbeSupportsGather(receiverProbe);
#endif
#endif
        // Reject lifecycle-invalid records immediately after the single aligned
        // compact load. No address math, atlas load, or texture sample is
        // reachable from this path in receiver shaders.
        if (!stateSupported)
        {
#if NJULF_DDGI_DETAILED_COUNTERS
            result.combinedRejectionMask |= rejectionMask;
            if (result.firstRejectionReason >= SIMPLE_DDGI_GATHER_REJECTION_REASON_COUNT)
                result.firstRejectionReason = uint(findLSB(rejectionMask));
            result.rejectedProbeCount++;
            RecordSimpleDdgiGatherRejectionMask(p, gatherRole, rejectionMask);
#endif
            continue;
        }

        SimpleDdgiAtlasAddress atlasAddress;
        if (!TryBuildSimpleDdgiAtlasAddress(
                p,
                volume,
                paging,
                atlasProbeAddress,
                atlasAddress))
        {
#if NJULF_DDGI_DETAILED_COUNTERS
            rejectionMask = SIMPLE_DDGI_GATHER_REJECT_INVALID_IRRADIANCE |
                SIMPLE_DDGI_GATHER_REJECT_INVALID_VISIBILITY;
            result.combinedRejectionMask |= rejectionMask;
            if (result.firstRejectionReason >= SIMPLE_DDGI_GATHER_REJECTION_REASON_COUNT)
                result.firstRejectionReason = uint(findLSB(rejectionMask));
            result.rejectedProbeCount++;
            RecordSimpleDdgiGatherRejectionMask(p, gatherRole, rejectionMask);
#endif
            continue;
        }

        vec3 probePos = volume.origin + vec3(c) * volume.spacing + relocation;
        vec3 toSurface = biasedWorldPos - probePos;
        float distanceToProbe = length(toSurface);
        vec3 probeToSurface = distanceToProbe > 0.00001 ? toSurface / distanceToProbe : safeNormal;
        vec4 irradiance = SampleSimpleDdgiIrradianceBilinearAtAddress(
            p.publishedIrradianceAtlasBufferIndex,
            atlasAddress,
            safeNormal,
            p.irradianceTexels,
            p);
        vec2 moments = SampleSimpleDdgiVisibilityBilinearAtAddress(
            uint(SIMPLE_DDGI_VISIBILITY_ATLAS_BUFFER_INDEX),
            atlasAddress,
            probeToSurface,
            p.visibilityTexels,
            p);
        bool atlasSupported;
#if NJULF_DDGI_DETAILED_COUNTERS
        rejectionMask = SimpleDdgiAtlasRejectionMask(irradiance);
        atlasSupported = rejectionMask == 0u;
#else
        atlasSupported = SimpleDdgiAtlasSupportsGather(irradiance);
#endif
        if (!atlasSupported)
        {
#if NJULF_DDGI_DETAILED_COUNTERS
            result.combinedRejectionMask |= rejectionMask;
            if (result.firstRejectionReason >= SIMPLE_DDGI_GATHER_REJECTION_REASON_COUNT)
                result.firstRejectionReason = uint(findLSB(rejectionMask));
            result.rejectedProbeCount++;
            RecordSimpleDdgiGatherRejectionMask(p, gatherRole, rejectionMask);
#endif
            continue;
        }

        float halfLambert = clamp(dot(safeNormal, -probeToSurface) * 0.5 + 0.5, 0.0, 1.0);
        // Directional weighting chooses representative probes; it is not missing
        // data and must not remove energy from an otherwise valid interpolation
        // cell.  Keep a tiny directional floor so an entirely back-facing cell can
        // still produce a stable estimate while visibility remains authoritative.
        float directionalWeight = max(halfLambert * halfLambert, 1.0e-4);
        float dataWeight = trilinear * clamp(activeWeight, 0.0, 1.0);
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
#if NJULF_DDGI_DETAILED_COUNTERS && NJULF_SIMPLE_DDGI_SOURCE_CACHE_DIAGNOSTIC
        if (p.debugView == SIMPLE_DDGI_DEBUG_SOURCE_CACHE_RADIANCE)
        {
#if SIMPLE_DDGI_GATHER_USES_COMPUTE_STATE
            SimpleDdgiProbeState sourceDebugState = state;
#else
            // The source cache is producer-only data. Detailed investigation
            // builds may inspect its generation metadata, but production
            // receiver SPIR-V contains no 32-byte state load or ray loop.
            SimpleDdgiProbeState sourceDebugState = ReadSimpleDdgiProbeState(
                uint(SIMPLE_DDGI_PROBE_STATE_BUFFER_INDEX),
                probeIndex);
#endif
            sourceCacheAccumulated += SampleSimpleDdgiProbeSourceCacheIrradiance(
                p,
                volumeIndex,
                probeIndex,
                probeAddress.physicalProbeIndex,
                sourceDebugState,
                safeNormal) * selectedDirectionalWeight;
        }
#endif
        availableMass += dataWeight;
        validMass += selectedDataWeight;
        directionalMass += selectedDirectionalWeight;
        visibleMass += selectedDirectionalWeight * transportVisibility;
#if NJULF_DDGI_DETAILED_COUNTERS
        validProbeCount++;
#endif
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
    result.irradiance = directionalMass > 0.000001
        ? clamp(accumulated / directionalMass, vec3(0.0), vec3(64.0))
        : vec3(0.0);
#if NJULF_DDGI_DETAILED_COUNTERS
    result.sourceCacheIrradiance = directionalMass > 0.000001
        ? clamp(sourceCacheAccumulated / directionalMass, vec3(0.0), vec3(64.0))
        : vec3(0.0);
    result.contributingVolumeColor = directionalMass > 0.000001
        ? SimpleDdgiVolumeContributorDebugColor(volumeIndex, volume.kind)
        : vec3(0.0);
    result.validProbeCount = validProbeCount;
    if (validProbeCount == 0u && result.spatialCoverage > 0.000001)
    {
        AddSimpleDdgiGatherDiagnostic(
            p,
            p.frameIndex % uint(FRAMES_IN_FLIGHT),
            SIMPLE_DDGI_GATHER_ALL_FAILED_COUNTER_BASE +
                min(gatherRole, SIMPLE_DDGI_GATHER_ROLE_COUNT - 1u));
    }
#endif
#if NJULF_SIMPLE_DDGI_GATHER_ATTRIBUTION
    result.primaryContributionWeight = directionalMass > 0.000001 ? 1.0 : 0.0;
#endif
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
#if NJULF_DDGI_DETAILED_COUNTERS
    vec3 sourceCacheAccumulated = outer.sourceCacheIrradiance * outerDirectionalMass +
        inner.sourceCacheIrradiance * innerDirectionalMass;
    vec3 contributorColorAccumulated = outer.contributingVolumeColor * outerDirectionalMass +
        inner.contributingVolumeColor * innerDirectionalMass;
#endif
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
    result.nonResidentProbeCount =
        inner.nonResidentProbeCount + outer.nonResidentProbeCount;
    result.irradiance = directionalMass > 0.000001
        ? clamp(accumulated / directionalMass, vec3(0.0), vec3(64.0))
        : vec3(0.0);
#if NJULF_DDGI_DETAILED_COUNTERS
    result.sourceCacheIrradiance = directionalMass > 0.000001
        ? clamp(sourceCacheAccumulated / directionalMass, vec3(0.0), vec3(64.0))
        : vec3(0.0);
    result.contributingVolumeColor = directionalMass > 0.000001
        ? clamp(contributorColorAccumulated / directionalMass, vec3(0.0), vec3(1.0))
        : vec3(0.0);
#endif
#if NJULF_SIMPLE_DDGI_GATHER_ATTRIBUTION
    result.selectedVolume = inner.selectedVolume;
    result.secondaryVolume = outer.selectedVolume;
    result.primaryContributionWeight = directionalMass > 0.000001
        ? clamp(innerDirectionalMass / directionalMass, 0.0, 1.0)
        : 0.0;
    result.secondaryContributionWeight = directionalMass > 0.000001
        ? clamp(outerDirectionalMass / directionalMass, 0.0, 1.0)
        : 0.0;
#endif
#if NJULF_DDGI_DETAILED_COUNTERS
    result.transitionWeight = w;
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
#endif
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
#if NJULF_DDGI_DETAILED_COUNTERS
        RecordSimpleDdgiGatherRejectionMask(
            p,
            SIMPLE_DDGI_GATHER_ROLE_PRIMARY,
            SIMPLE_DDGI_GATHER_REJECT_OUTSIDE_DOMAIN);
#endif
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
#if !SIMPLE_DDGI_GATHER_USES_COMPUTE_STATE
    if (selected.nonResidentProbeCount != 0u)
    {
        SimpleDdgiRecordResidencyCounter(
            p,
            SIMPLE_DDGI_RESIDENCY_COUNTER_NONRESIDENT_GATHER_REJECTIONS,
            selected.nonResidentProbeCount);
    }
#endif
#if NJULF_DDGI_DETAILED_COUNTERS
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
#endif
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
#if NJULF_DDGI_DETAILED_COUNTERS
        AddSimpleDdgiGatherDiagnostic(
            p,
            diagnosticFrame,
            SIMPLE_DDGI_ONE_GATHER_PIXEL_COUNTER);
#endif
        selected.spatialCoverage *= edgeWeight;
        selected.ownership *= edgeWeight;
#if NJULF_DDGI_DETAILED_COUNTERS
        selected.transitionWeight = edgeWeight;
#endif
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
#if !SIMPLE_DDGI_GATHER_USES_COMPUTE_STATE
        if (selected.nonResidentProbeCount != 0u)
        {
            SimpleDdgiRecordResidencyCounter(
                p,
                SIMPLE_DDGI_RESIDENCY_COUNTER_COARSER_FALLBACK,
                1u);
        }
#endif
#if NJULF_DDGI_DETAILED_COUNTERS
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
        else if (selected.validSupport <= 0.000001)
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
#endif

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
#if NJULF_DDGI_DETAILED_COUNTERS
        AddSimpleDdgiGatherDiagnostic(
            p,
            diagnosticFrame,
            DDGI_INVESTIGATION_SIMPLE_SECOND_VOLUME_GATHER_COUNTER);
        AddSimpleDdgiGatherDiagnostic(
            p,
            diagnosticFrame,
            DDGI_INVESTIGATION_SIMPLE_VOLUME_SAMPLED_GATHER_COUNTER_BASE + fallbackVolumeIndex);
#endif
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
#if NJULF_DDGI_DETAILED_COUNTERS
        bool recoveryGathered = false;
#endif
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
#if NJULF_DDGI_DETAILED_COUNTERS
                recoveryGathered = true;
                AddSimpleDdgiGatherDiagnostic(
                    p,
                    diagnosticFrame,
                    DDGI_INVESTIGATION_SIMPLE_VOLUME_SAMPLED_GATHER_COUNTER_BASE + recoveryVolumeIndex);
#endif
                float combinedDirectionalAuthority =
                    SimpleDdgiDirectionalGatherAuthority(combined);
                combined = BlendSimpleDdgiGatherResults(
                    recovery,
                    combined,
                    combinedDirectionalAuthority);
            }
        }

#if NJULF_DDGI_DETAILED_COUNTERS
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
#endif

        return combined;
    }

    // At the outer edge only ownership fades.  Irradiance stays normalized so the
    // caller can compose the represented DDGI share and the missing environment
    // share exactly once.
    selected.spatialCoverage *= edgeWeight;
    selected.ownership *= edgeWeight;
#if NJULF_DDGI_DETAILED_COUNTERS
    selected.transitionWeight = edgeWeight;
    AddSimpleDdgiGatherDiagnostic(
        p,
        diagnosticFrame,
        SIMPLE_DDGI_ONE_GATHER_PIXEL_COUNTER);
#endif
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
    SimpleDdgiVolumePaging paging = ReadSimpleDdgiVolumePaging(
        uint(SIMPLE_DDGI_PARAMS_BUFFER_INDEX),
        selectedVolumeIndex);
    SimpleDdgiProbeAddress probeAddress = ResolveSimpleDdgiProbeAddress(
        p,
        volume,
        paging,
        uvec3(nearest));
    vec3 relocation = vec3(0.0);
    uint atlasProbeAddress = SIMPLE_DDGI_RECEIVER_INVALID_ATLAS_ADDRESS;
#if SIMPLE_DDGI_GATHER_USES_COMPUTE_STATE
    if (probeAddress.resident && probeAddress.published)
    {
        SimpleDdgiProbeState state = ReadSimpleDdgiProbeState(
            uint(SIMPLE_DDGI_PROBE_STATE_BUFFER_INDEX),
            probeIndex);
        relocation = state.relocation;
        atlasProbeAddress = probeAddress.physicalProbeIndex;
    }
#else
    if (probeAddress.resident && probeAddress.published)
    {
        SimpleDdgiReceiverProbe receiverProbe = ReadSimpleDdgiReceiverProbe(
            uint(SIMPLE_DDGI_RECEIVER_PROBE_BUFFER_INDEX),
            probeIndex,
            volume.spacing);
        if (SimpleDdgiReceiverProbeSupportsGather(receiverProbe) &&
            receiverProbe.atlasProbeAddress ==
                probeAddress.physicalProbeIndex)
        {
            relocation = receiverProbe.relocation;
            atlasProbeAddress = probeAddress.physicalProbeIndex;
        }
    }
#endif
    vec3 probePos = logicalProbePos + relocation;
    vec3 toSurface = interpolationPosition - probePos;
    float distanceToProbe = length(toSurface);
    vec3 probeToSurface = distanceToProbe > 0.00001 ? toSurface / distanceToProbe : normalize(normal);
    SimpleDdgiAtlasAddress atlasAddress;
    vec2 moments = vec2(0.0);
    if (TryBuildSimpleDdgiAtlasAddress(
            p,
            volume,
            paging,
            atlasProbeAddress,
            atlasAddress))
    {
        moments = SampleSimpleDdgiVisibilityBilinearAtAddress(
            uint(SIMPLE_DDGI_VISIBILITY_ATLAS_BUFFER_INDEX),
            atlasAddress,
            probeToSurface,
            p.visibilityTexels,
            p);
    }
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
    result.residencyTableFlags = probeAddress.published
        ? SIMPLE_DDGI_PAGE_TABLE_VALID |
            SIMPLE_DDGI_PAGE_TABLE_PUBLISHED
        : 0u;
    result.residencyHistoryFlags = 0u;
    result.residencyDemandMask = 0u;
    result.physicalPageIndex = probeAddress.physicalPageIndex != 0xffffffffu
        ? probeAddress.physicalPageIndex
        : (probeAddress.resident
            ? probeAddress.physicalProbeIndex / SIMPLE_DDGI_PROBES_PER_PAGE
            : 0xffffffffu);
    result.pageMappingGeneration = probeAddress.pageMappingGeneration;
    result.pageAgeFrames = 0u;
    result.pageAgeNormalized = 0.0;
    if (probeAddress.virtualPageIndex != 0xffffffffu &&
        SimpleDdgiResidencyHeaderMatches(p))
    {
        uint tableBase = SIMPLE_DDGI_RESIDENCY_PAGE_TABLE_OFFSET_WORDS +
            probeAddress.virtualPageIndex *
                SIMPLE_DDGI_PAGE_TABLE_ENTRY_WORDS;
        uvec4 table = ReadStorageAlignedUVec4Uniform(
            p.residencyArenaBufferIndex,
            tableBase);
        uint historyBase = SimpleDdgiPageHistoryOffsetWords(p) +
            probeAddress.virtualPageIndex * SIMPLE_DDGI_PAGE_HISTORY_WORDS;
        uvec4 history = ReadStorageAlignedUVec4Uniform(
            p.residencyArenaBufferIndex,
            historyBase);
        result.residencyTableFlags = table.z;
        result.residencyHistoryFlags = history.w;
        uint currentEpoch = SimpleDdgiDemandEpochForFrame(p.frameIndex);
        uint previousEpoch = currentEpoch > 1u
            ? currentEpoch - 1u
            : SIMPLE_DDGI_DEMAND_EPOCH_MASK - 1u;
        uint visibleEpoch = SimpleDdgiDemandStampEpoch(history.x);
        uint receiverEpoch = SimpleDdgiDemandStampEpoch(history.y);
        if (visibleEpoch == currentEpoch || visibleEpoch == previousEpoch)
            result.residencyDemandMask |= 1u;
        if (receiverEpoch == currentEpoch || receiverEpoch == previousEpoch)
            result.residencyDemandMask |= 2u;
        if (p.frameIndex >= history.z)
            result.pageAgeFrames = p.frameIndex - history.z;
        uint retentionFrames = max(ReadStorageWordUniform(
            p.residencyArenaBufferIndex,
            11u), 1u);
        result.pageAgeNormalized = clamp(
            float(result.pageAgeFrames) / float(retentionFrames),
            0.0,
            1.0);
    }
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
