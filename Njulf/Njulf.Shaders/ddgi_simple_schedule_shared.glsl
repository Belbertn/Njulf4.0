#ifndef NJULF_DDGI_SIMPLE_SCHEDULE_SHARED_GLSL
#define NJULF_DDGI_SIMPLE_SCHEDULE_SHARED_GLSL

#include "common.glsl"
#include "ddgi_simple_storage_abi.glsl"
#include "ddgi_simple_receiver_abi.glsl"
#include "ddgi_simple_scheduler_metadata_abi.glsl"
#include "ddgi_receiver_feedback_summary_abi.glsl"

// This file is the shader-side mirror of SimpleDdgiGpuSchedulerLayout and
// GPUSimpleDdgiSchedulePushConstants. Keep all offsets in words and derive
// them from the pushed layout; the arena can grow without changing SPIR-V.
const uint SIMPLE_DDGI_SCHEDULER_MAX_VOLUMES = 16u;
const uint SIMPLE_DDGI_SCHEDULER_MAX_LANES = 896u;
const uint SIMPLE_DDGI_SCHEDULER_WORK_CLASSES = 7u;
const uint SIMPLE_DDGI_SCHEDULER_TRANSPORT_CATEGORIES = 4u;
const uint SIMPLE_DDGI_SCHEDULER_RAY_TIERS = 2u;
const uint SIMPLE_DDGI_SCHEDULER_MAX_RAYS = 256u;
const uint SIMPLE_DDGI_SCHEDULER_INVALID_PROBE = 0xffffffffu;
const uint SIMPLE_DDGI_SCHEDULER_WORKGROUP_SIZE = 64u;
const uint SIMPLE_DDGI_SCHEDULER_FEEDBACK_WORDS = 1024u;
const uint SIMPLE_DDGI_SCHEDULER_VOLUME_POLICY_WORDS = 44u;
// The public candidate record is eight words. Resident classification stores
// only probe, volume, class/transport, and tier/reasons. Admission re-reads the
// immutable generation, uses the input index as sequence ordinal, and derives
// ray counts from policy, keeping the large pool at a 16-byte stride.
const uint SIMPLE_DDGI_SCHEDULER_CANDIDATE_WORDS = 4u;
const uint SIMPLE_DDGI_SCHEDULER_COMPACT_OUTPUT_WORDS = 1u;
const uint SIMPLE_DDGI_SCHEDULER_GROUP_LANE_VALUES_PER_WORD = 2u;
// Public probe state and private scheduler state are different ABIs. Keep the
// public stride here because several scheduler shaders read the public buffer;
// the cross-subsystem private stride lives in scheduler_metadata_abi.glsl.
const uint SIMPLE_DDGI_PROBE_STATE_WORDS = 8u;
// The public queue is twelve words so the predecessor's exact outcome/source
// epoch fields and sparse physical identity coexist while every record remains
// 16-byte aligned. The internal proposal appends physical index and mapping
// generation to its original seven words.
const uint SIMPLE_DDGI_PROBE_UPDATE_STRIDE_WORDS = 12u;
const uint SIMPLE_DDGI_SCHEDULER_UPDATE_WORDS = 10u;
const uint SIMPLE_DDGI_SCHEDULER_OUTCOME_WORDS = 15u;
const uint SIMPLE_DDGI_SCHEDULER_QUADRATURE_WITNESS_VALID = 1u << 31u;
const float SIMPLE_DDGI_SCHEDULER_QUADRATURE_ERROR_THRESHOLD = 0.05;
// The transaction-private relocation proposal reuses the existing 48-byte
// relocation/classification storage ABI. Commit is a scheduler-only shader,
// so mirror that stride here rather than depending on the producer header.
const uint SIMPLE_DDGI_RELOCATION_CLASSIFICATION_STRIDE_WORDS = 12u;
const uint SIMPLE_DDGI_SCHEDULER_INDIRECT_WORDS = 4u;
const uint SIMPLE_DDGI_SCHEDULER_IRRADIANCE_WORDS_PER_PROBE = 128u;
// Canonical visibility is one RG16F moment pair (one uint word) per 16x16
// texel. Keep this independent from the RGBA16F irradiance stride.
const uint SIMPLE_DDGI_SCHEDULER_VISIBILITY_WORDS_PER_PROBE = 256u;
const uint SIMPLE_DDGI_SCHEDULER_NEAR_VISIBILITY_WORDS_PER_PROBE =
    SIMPLE_DDGI_SCHEDULER_VISIBILITY_WORDS_PER_PROBE * 2u;
const uint SIMPLE_DDGI_FLAG_NEAR_VISIBILITY_SIDECAR = 1u << 31u;
const uint SIMPLE_DDGI_SCHEDULER_PARAMS_FLAGS_WORD = 14u;
const uint SIMPLE_DDGI_SCHEDULER_PARAMS_PHYSICAL_CAPACITY_WORD = 54u;
const uint SIMPLE_DDGI_SCHEDULER_VOLUME_KIND_RING = 2u;
const uint SIMPLE_DDGI_SCHEDULER_VOLUME_KIND_REFINEMENT = 3u;
// Ray-bucket commands and queue metadata are separate regions. The command
// region is passed directly to vkCmdDispatchIndirect; metadata is never part
// of a VkDispatchIndirectCommand.
const uint SIMPLE_DDGI_SCHEDULER_RAY_BUCKET_METADATA_WORDS = 4u;
const uint SIMPLE_DDGI_SCHEDULER_RAY_BUCKET_COMMAND_WORDS = 4u;
const uint SIMPLE_DDGI_SCHEDULER_COUNTER_WORDS = 96u;
// GPUSimpleDdgiParams word 63 carries a one-based arena offset. Keep the
// compact two-word/two-bank ABI synchronized with ddgi_simple_shared.glsl and
// SimpleDdgiGpuSchedulerLayout.
const uint SIMPLE_DDGI_SCHEDULER_RECEIVER_OFFSET_PARAM_WORD = 63u;
const uint SIMPLE_DDGI_SCHEDULER_RECEIVER_BANK_COUNT = 2u;
const uint SIMPLE_DDGI_SCHEDULER_RECEIVER_WORDS_PER_PROBE = 2u;
const uint SIMPLE_DDGI_SCHEDULER_RECEIVER_COVERAGE_MASK = 0x00ffffffu;
const uint SIMPLE_DDGI_SCHEDULER_RECEIVER_ROLE_MASK = 0x07000000u;
const uint SIMPLE_DDGI_SCHEDULER_RECEIVER_FALLBACK_MASK = 0x06000000u;
const uint SIMPLE_DDGI_SCHEDULER_RECEIVER_REFLECTION = 1u << 31u;
const float SIMPLE_DDGI_SCHEDULER_RECEIVER_WEIGHT_SCALE = 1.0 / 256.0;

// Per-request producer completion. A request is committable only when every
// required bit is set and no failure reason was recorded.
const uint SIMPLE_DDGI_SCHEDULER_COMPLETE_TRACE = 1u << 0u;
const uint SIMPLE_DDGI_SCHEDULER_COMPLETE_RELOCATE = 1u << 1u;
const uint SIMPLE_DDGI_SCHEDULER_COMPLETE_TRANSPORT = 1u << 2u;
const uint SIMPLE_DDGI_SCHEDULER_TRANSPORT_NOT_REQUIRED = 1u << 3u;
const uint SIMPLE_DDGI_SCHEDULER_COMPLETE_BLEND = 1u << 4u;
const uint SIMPLE_DDGI_SCHEDULER_COMPLETE_CANONICAL_PUBLISH = 1u << 5u;
const uint SIMPLE_DDGI_SCHEDULER_COMPLETE_SAMPLED_PUBLISH = 1u << 6u;
const uint SIMPLE_DDGI_SCHEDULER_SAMPLED_PUBLISH_NOT_REQUIRED = 1u << 7u;
const uint SIMPLE_DDGI_SCHEDULER_COMPLETE_URGENT_COMMIT = 1u << 31u;
const uint SIMPLE_DDGI_SCHEDULER_FEATURE_SAMPLED_PUBLICATION = 1u << 8u;
const uint SIMPLE_DDGI_SCHEDULER_FEATURE_TAIL_CERTIFICATION = 1u << 9u;
const uint SIMPLE_DDGI_SCHEDULER_FEATURE_PERIODIC_SOURCE_WAVE = 1u << 10u;
const uint SIMPLE_DDGI_SCHEDULER_FEATURE_CERTIFIED_QUIESCED = 1u << 11u;
const uint SIMPLE_DDGI_SCHEDULER_FEATURE_COST_AWARE_PRIORITY = 1u << 12u;
const uint SIMPLE_DDGI_SCHEDULER_FEATURE_SPARSE_RESIDUAL_PROPAGATION = 1u << 13u;
const uint SIMPLE_DDGI_SCHEDULER_FEATURE_EXACT_RECEIVER_FEEDBACK = 1u << 14u;
const uint SIMPLE_DDGI_SCHEDULER_FEATURE_ADAPTIVE_RAY_CARDINALITY = 1u << 15u;
const uint SIMPLE_DDGI_SCHEDULER_FEATURE_RESET_LANE_CURSORS = 1u << 16u;
const uint SIMPLE_DDGI_SCHEDULER_FEATURE_DEFER_RADIOMETRIC_PUBLICATION =
    1u << 17u;
const uint SIMPLE_DDGI_SCHEDULER_EXACT_FEEDBACK_BINDING_VALID = 1u << 0u;

const uint SIMPLE_DDGI_SCHEDULER_FRAME_EXACT_FEEDBACK_BUFFER = 44u;
const uint SIMPLE_DDGI_SCHEDULER_FRAME_EXACT_FEEDBACK_BANK_OFFSET = 45u;
const uint SIMPLE_DDGI_SCHEDULER_FRAME_EXACT_FEEDBACK_BANK_STRIDE = 46u;
const uint SIMPLE_DDGI_SCHEDULER_FRAME_EXACT_FEEDBACK_RECORD_CAPACITY = 47u;
const uint SIMPLE_DDGI_SCHEDULER_FRAME_EXACT_FEEDBACK_SUMMARY_CAPACITY = 48u;
const uint SIMPLE_DDGI_SCHEDULER_FRAME_EXACT_FEEDBACK_FALLBACK_CAPACITY = 49u;
const uint SIMPLE_DDGI_SCHEDULER_FRAME_EXACT_FEEDBACK_GENERATION = 50u;
const uint SIMPLE_DDGI_SCHEDULER_FRAME_EXACT_FEEDBACK_VIEWPORT = 51u;
const uint SIMPLE_DDGI_SCHEDULER_FRAME_EXACT_FEEDBACK_FRAME_LOW = 52u;
const uint SIMPLE_DDGI_SCHEDULER_FRAME_EXACT_FEEDBACK_FRAME_HIGH = 53u;
const uint SIMPLE_DDGI_SCHEDULER_FRAME_EXACT_FEEDBACK_FLAGS = 54u;

const uint SIMPLE_DDGI_SCHEDULER_FAILURE_INVALID_GENERATION = 1u << 0u;
const uint SIMPLE_DDGI_SCHEDULER_FAILURE_PRODUCER = 1u << 1u;
const uint SIMPLE_DDGI_SCHEDULER_FAILURE_PARTIAL = 1u << 2u;
const uint SIMPLE_DDGI_SCHEDULER_FAILURE_PUBLIC_WRITE = 1u << 3u;

const uint SIMPLE_DDGI_SCHEDULER_WORK_VISIBLE_ZERO_SUPPORT = 0u;
const uint SIMPLE_DDGI_SCHEDULER_WORK_FRESH_EXPOSED_VISIBLE = 1u;
const uint SIMPLE_DDGI_SCHEDULER_WORK_VISIBLE_DIRTY = 2u;
const uint SIMPLE_DDGI_SCHEDULER_WORK_VISIBLE_RETRY = 3u;
const uint SIMPLE_DDGI_SCHEDULER_WORK_NEAR_MAINTENANCE = 4u;
const uint SIMPLE_DDGI_SCHEDULER_WORK_MID_MAINTENANCE = 5u;
const uint SIMPLE_DDGI_SCHEDULER_WORK_FAR_MAINTENANCE = 6u;

const uint SIMPLE_DDGI_SCHEDULER_TRANSPORT_HARD_SOURCE = 0u;
const uint SIMPLE_DDGI_SCHEDULER_TRANSPORT_ROUTINE_SOURCE = 1u;
const uint SIMPLE_DDGI_SCHEDULER_TRANSPORT_CACHED_SOLVER = 2u;
const uint SIMPLE_DDGI_SCHEDULER_TRANSPORT_MAINTENANCE = 3u;

const uint SIMPLE_DDGI_SCHEDULER_RAY_FULL = 0u;
const uint SIMPLE_DDGI_SCHEDULER_RAY_MAINTENANCE = 1u;

const uint SIMPLE_DDGI_SCHEDULER_REASON_FRESH = 1u << 0u;
const uint SIMPLE_DDGI_SCHEDULER_REASON_SCROLL = 1u << 1u;
const uint SIMPLE_DDGI_SCHEDULER_REASON_REGIONAL_DIRTY = 1u << 2u;
const uint SIMPLE_DDGI_SCHEDULER_REASON_GLOBAL_DIRTY = 1u << 3u;
const uint SIMPLE_DDGI_SCHEDULER_REASON_VISIBLE = 1u << 4u;
const uint SIMPLE_DDGI_SCHEDULER_REASON_RETRY = 1u << 5u;
const uint SIMPLE_DDGI_SCHEDULER_REASON_RELOCATION_RETRY = 1u << 6u;
const uint SIMPLE_DDGI_SCHEDULER_REASON_SOURCE_INVALID = 1u << 7u;
const uint SIMPLE_DDGI_SCHEDULER_REASON_ROUTINE_DUE = 1u << 8u;
const uint SIMPLE_DDGI_SCHEDULER_REASON_CONVERGENCE = 1u << 9u;
const uint SIMPLE_DDGI_SCHEDULER_REASON_INACTIVE_RETRY = 1u << 10u;
const uint SIMPLE_DDGI_SCHEDULER_REASON_TOPOLOGY = 1u << 11u;
const uint SIMPLE_DDGI_SCHEDULER_REASON_VISIBLE_PAGE_COHORT = 1u << 12u;
const uint SIMPLE_DDGI_SCHEDULER_REASON_RADIOMETRIC_RELIGHT = 1u << 13u;
const uint SIMPLE_DDGI_SCHEDULER_REASON_SEGMENT_SELECTIVE = 1u << 14u;
const uint SIMPLE_DDGI_SCHEDULER_REASON_RESIDUAL_PROPAGATION = 1u << 15u;
// Compatibility alias for diagnostics/tests authored with the first
// radiometric-only mode. The bit now covers environment misses and exact
// sole-directional cached hits.
const uint SIMPLE_DDGI_SCHEDULER_REASON_ENVIRONMENT_RELIGHT =
    SIMPLE_DDGI_SCHEDULER_REASON_RADIOMETRIC_RELIGHT;

// Scheduler-private visibility/publication bits live in the high half of the
// dirty-reason word. The low reason bits are transient; these two bits survive
// commit so the delayed atmosphere summary can identify the visible cohort
// without a CPU probe scan.
const uint SIMPLE_DDGI_SCHEDULER_PROBE_FRESH = 1u << 0u;
const uint SIMPLE_DDGI_SCHEDULER_PROBE_SCROLL = 1u << 1u;
const uint SIMPLE_DDGI_SCHEDULER_PROBE_INACTIVE = 1u << 2u;
const uint SIMPLE_DDGI_PROBE_FLAG_FRESH = 1u << 0u;
const uint SIMPLE_DDGI_PROBE_FLAG_SCROLL_EXPOSED = 1u << 1u;
const uint SIMPLE_DDGI_PROBE_FLAG_INACTIVE = 1u << 2u;
const uint SIMPLE_DDGI_PROBE_FLAG_RELOCATION_PENDING = 1u << 3u;
const uint SIMPLE_DDGI_PROBE_FLAG_SOURCE_CACHE_INVALID = 1u << 4u;
const uint SIMPLE_DDGI_PROBE_FLAG_VISIBILITY_VALID = 1u << 5u;
const uint SIMPLE_DDGI_PROBE_FLAG_NON_RESIDENT = 1u << 6u;
const uint SIMPLE_DDGI_PROBE_FLAG_GENERATION_SHIFT = 8u;
const uint SIMPLE_DDGI_PROBE_FLAG_GENERATION_MASK = 0xffffff00u;
const uint SIMPLE_DDGI_SCHEDULER_PROBE_RELOCATION = 1u << 3u;
const uint SIMPLE_DDGI_SCHEDULER_PROBE_SOURCE_INVALID = 1u << 4u;
const uint SIMPLE_DDGI_SCHEDULER_PROBE_GENERATION_SHIFT = 8u;
const uint SIMPLE_DDGI_SCHEDULER_PROBE_GENERATION_MASK = 0xffffff00u;
const uint SIMPLE_DDGI_SCHEDULER_UPDATE_MAINTENANCE = 1u << 12u;
const uint SIMPLE_DDGI_SCHEDULER_UPDATE_SOURCE_REFRESH = 1u << 13u;
const uint SIMPLE_DDGI_SCHEDULER_UPDATE_ROUTINE_SOURCE = 1u << 15u;
const uint SIMPLE_DDGI_SCHEDULER_UPDATE_INVALIDATE = 1u << 14u;
const uint SIMPLE_DDGI_SCHEDULER_UPDATE_RAY_SHIFT = 16u;
const uint SIMPLE_DDGI_SCHEDULER_UPDATE_RAY_MASK = 0x1ffu <<
    SIMPLE_DDGI_SCHEDULER_UPDATE_RAY_SHIFT;
const uint SIMPLE_DDGI_SCHEDULER_UPDATE_SOURCE_MODE_SHIFT = 30u;
const uint SIMPLE_DDGI_SCHEDULER_UPDATE_SOURCE_MODE_MASK = 0x3u <<
    SIMPLE_DDGI_SCHEDULER_UPDATE_SOURCE_MODE_SHIFT;
const uint SIMPLE_DDGI_SCHEDULER_SOURCE_MODE_FULL_TRACE = 0u;
const uint SIMPLE_DDGI_SCHEDULER_SOURCE_MODE_ENVIRONMENT_RELIGHT = 1u;
const uint SIMPLE_DDGI_SCHEDULER_SOURCE_MODE_CACHED_HIT_RELIGHT = 2u;
const uint SIMPLE_DDGI_SCHEDULER_SOURCE_MODE_SEGMENT_SELECTIVE = 3u;
const uint SIMPLE_DDGI_SCHEDULER_PRIVATE_REASON_VISIBLE = 1u << 25u;
const uint SIMPLE_DDGI_SCHEDULER_PRIVATE_REASON_REGIONAL = 1u << 26u;
const uint SIMPLE_DDGI_SCHEDULER_PRIVATE_REASON_GLOBAL = 1u << 27u;
const uint SIMPLE_DDGI_SCHEDULER_PRIVATE_REASON_SOURCE = 1u << 28u;
const uint SIMPLE_DDGI_SCHEDULER_PRIVATE_REASON_TOPOLOGY = 1u << 29u;
const uint SIMPLE_DDGI_SCHEDULER_PRIVATE_REASON_MASK =
    SIMPLE_DDGI_SCHEDULER_PRIVATE_REASON_VISIBLE |
    SIMPLE_DDGI_SCHEDULER_PRIVATE_REASON_REGIONAL |
    SIMPLE_DDGI_SCHEDULER_PRIVATE_REASON_GLOBAL |
    SIMPLE_DDGI_SCHEDULER_PRIVATE_REASON_SOURCE |
    SIMPLE_DDGI_SCHEDULER_PRIVATE_REASON_TOPOLOGY;
const uint SIMPLE_DDGI_SCHEDULER_SOURCE_RAY_MASK = 0x1ffu;
const uint SIMPLE_DDGI_SCHEDULER_TRANSPORT_GENERATION_SHIFT = 9u;
const uint SIMPLE_DDGI_SCHEDULER_TRANSPORT_GENERATION_MASK = 0xffu <<
    SIMPLE_DDGI_SCHEDULER_TRANSPORT_GENERATION_SHIFT;
const uint SIMPLE_DDGI_SCHEDULER_STABLE_UPDATE_SHIFT = 17u;
const uint SIMPLE_DDGI_SCHEDULER_STABLE_UPDATE_MASK = 0xffu <<
    SIMPLE_DDGI_SCHEDULER_STABLE_UPDATE_SHIFT;

// Counter words are intentionally fixed and are part of the feedback ABI.
const uint SIMPLE_DDGI_SCHEDULER_COUNTER_CONSIDERED = 0u;
const uint SIMPLE_DDGI_SCHEDULER_COUNTER_ELIGIBLE = 1u;
const uint SIMPLE_DDGI_SCHEDULER_COUNTER_ACCEPTED = 2u;
const uint SIMPLE_DDGI_SCHEDULER_COUNTER_COMMITTED = 3u;
const uint SIMPLE_DDGI_SCHEDULER_COUNTER_REJECTED = 4u;
const uint SIMPLE_DDGI_SCHEDULER_COUNTER_INVALID_GENERATION = 5u;
const uint SIMPLE_DDGI_SCHEDULER_COUNTER_OVERFLOW = 6u;
const uint SIMPLE_DDGI_SCHEDULER_COUNTER_COMPACTED = 8u;
// A committed resident transaction has copied the private transport atlas to
// the receiver-visible canonical atlas. This witness is distinct from the
// generic commit count, which also includes source-only lifecycle commits.
const uint SIMPLE_DDGI_SCHEDULER_COUNTER_PUBLISHED = 7u;
const uint SIMPLE_DDGI_SCHEDULER_COUNTER_PRIMARY_USED = 9u;
const uint SIMPLE_DDGI_SCHEDULER_COUNTER_SOURCE_USED = 10u;
// Exact admitted work witnesses. SOURCE_PROBE_USED counts transactions that
// execute a complete source-ray sequence; TRANSPORT_USED includes both those
// source transactions and cached solver evaluations.
const uint SIMPLE_DDGI_SCHEDULER_COUNTER_TRANSPORT_USED = 11u;
const uint SIMPLE_DDGI_SCHEDULER_COUNTER_SOURCE_PROBE_USED = 12u;
const uint SIMPLE_DDGI_SCHEDULER_COUNTER_HARD_SOURCE_PROBE_USED = 13u;
const uint SIMPLE_DDGI_SCHEDULER_COUNTER_ROUTINE_SOURCE_PROBE_USED = 14u;
const uint SIMPLE_DDGI_SCHEDULER_COUNTER_CACHED_SOLVER_PROBE_USED = 15u;
const uint SIMPLE_DDGI_SCHEDULER_COUNTER_BUCKET_BASE = 20u;
const uint SIMPLE_DDGI_SCHEDULER_COUNTER_VOLUME_USAGE_BASE = 32u;
const uint SIMPLE_DDGI_SCHEDULER_COUNTER_CLASS_USAGE_BASE = 16u;
const uint SIMPLE_DDGI_SCHEDULER_COUNTER_PENDING_TAIL_SOURCE = 48u;
// Admission rejection is a scheduling-pressure metric. Keep publication
// failures separate so a failed resident transaction cannot be hidden by the
// much larger population that was legitimately deferred by the frame budget.
const uint SIMPLE_DDGI_SCHEDULER_COUNTER_COMMIT_REJECTED = 49u;
// Commit failures are exceptional and already pay the generic rejected
// atomic. Preserve a bounded stage breakdown in otherwise-unused counter
// words so a source-repair loop can be diagnosed without a per-probe readback.
const uint SIMPLE_DDGI_SCHEDULER_COUNTER_COMMIT_FAILURE_BASE = 50u;
const uint SIMPLE_DDGI_SCHEDULER_COMMIT_FAILURE_CATEGORY_COUNT = 10u;
const uint SIMPLE_DDGI_SCHEDULER_COMMIT_FAILURE_TRANSACTION = 0u;
const uint SIMPLE_DDGI_SCHEDULER_COMMIT_FAILURE_PUBLIC_GENERATION = 1u;
const uint SIMPLE_DDGI_SCHEDULER_COMMIT_FAILURE_SOURCE_EPOCH = 2u;
const uint SIMPLE_DDGI_SCHEDULER_COMMIT_FAILURE_PAYLOAD_IDENTITY = 3u;
const uint SIMPLE_DDGI_SCHEDULER_COMMIT_FAILURE_CACHE_PRECONDITION = 4u;
const uint SIMPLE_DDGI_SCHEDULER_COMMIT_FAILURE_CACHE_ADDRESS = 5u;
const uint SIMPLE_DDGI_SCHEDULER_COMMIT_FAILURE_CACHE_CLASSIFICATION = 6u;
const uint SIMPLE_DDGI_SCHEDULER_COMMIT_FAILURE_CACHE_GENERATION = 7u;
const uint SIMPLE_DDGI_SCHEDULER_COMMIT_FAILURE_CACHE_EPOCH_OR_CARDINALITY = 8u;
const uint SIMPLE_DDGI_SCHEDULER_COMMIT_FAILURE_RECEIVER_PACK = 9u;
const uint SIMPLE_DDGI_SCHEDULER_COUNTER_TRANSACTION_PREDICATE_MASK = 60u;
const uint SIMPLE_DDGI_SCHEDULER_COUNTER_MISSING_COMPLETION_MASK = 61u;
const uint SIMPLE_DDGI_SCHEDULER_COUNTER_PRODUCER_FAILURE_MASK = 62u;
const uint SIMPLE_DDGI_SCHEDULER_COUNTER_CACHE_READ_FAILURE_MASK = 63u;
// Classification appends one exact source-candidate count per volume for
// unpublished pages that were allocated from visible/receiver demand. These
// counters are scheduler-private and intentionally sit after the fixed
// diagnostic counter prefix.
const uint SIMPLE_DDGI_SCHEDULER_COUNTER_VISIBLE_PAGE_COHORT_BASE = 64u;
// Exact classifier output. These are incremented only after all candidate
// eligibility gates succeed, before budget admission may defer the work.
const uint SIMPLE_DDGI_SCHEDULER_COUNTER_ELIGIBLE_CLASS_BASE = 80u;
const uint SIMPLE_DDGI_SCHEDULER_COUNTER_ELIGIBLE_RING_BASE = 87u;
const uint SIMPLE_DDGI_SCHEDULER_COUNTER_RESIDUAL_SEEDED = 90u;
const uint SIMPLE_DDGI_SCHEDULER_COUNTER_RESIDUAL_DEPENDENT_WAKE = 91u;
const uint SIMPLE_DDGI_SCHEDULER_COUNTER_RESIDUAL_THRESHOLD_REJECT = 92u;
const uint SIMPLE_DDGI_SCHEDULER_COUNTER_RESIDUAL_FULL_SWEEP_FALLBACK = 93u;
// Words 94/95 survive the ordinary counter reset. Word 94 carries a
// frame-stamped accepted/committed urgent summary into the post-forward
// feedback reduction; word 95 is nonzero only while the pre-forward bounded
// transaction is being recorded.
const uint SIMPLE_DDGI_SCHEDULER_COUNTER_URGENT_TELEMETRY = 94u;
const uint SIMPLE_DDGI_SCHEDULER_COUNTER_URGENT_CONTROL = 95u;
const uint SIMPLE_DDGI_SCHEDULER_TRANSIENT_COUNTER_WORDS = 94u;

// Transaction-predicate witnesses are a bitset rather than counters: every
// failed request already contributes to the transaction category above, while
// this word identifies which invariant(s) were false without allocating a
// per-request readback. Multiple predicates may be recorded in one frame.
const uint SIMPLE_DDGI_SCHEDULER_TRANSACTION_QUEUE_GENERATION = 1u << 0u;
const uint SIMPLE_DDGI_SCHEDULER_TRANSACTION_RESOURCE_GENERATION = 1u << 1u;
const uint SIMPLE_DDGI_SCHEDULER_TRANSACTION_VOLUME_GENERATION = 1u << 2u;
const uint SIMPLE_DDGI_SCHEDULER_TRANSACTION_SOURCE_GENERATION = 1u << 3u;
const uint SIMPLE_DDGI_SCHEDULER_TRANSACTION_TRANSPORT_GENERATION = 1u << 4u;
const uint SIMPLE_DDGI_SCHEDULER_TRANSACTION_COMPLETION = 1u << 5u;
const uint SIMPLE_DDGI_SCHEDULER_TRANSACTION_PRODUCER_FAILURE = 1u << 6u;
const uint SIMPLE_DDGI_SCHEDULER_TRANSACTION_PROBE_BOUNDS = 1u << 7u;
const uint SIMPLE_DDGI_SCHEDULER_TRANSACTION_EXPECTED_GENERATION = 1u << 8u;
const uint SIMPLE_DDGI_SCHEDULER_TRANSACTION_INVALIDATION_WITHOUT_SOURCE =
    1u << 9u;

// The 4 KiB feedback region contains a 64-word header followed by 896 lane
// cursors. Its fixed tail carries bounded scheduler, convergence, receiver,
// and adaptive-cardinality evidence without growing either the arena or the
// delayed readback buffers.
const uint SIMPLE_DDGI_SCHEDULER_FEEDBACK_COMMIT_FAILURE_OFFSET = 960u;
const uint SIMPLE_DDGI_SCHEDULER_FEEDBACK_TRANSACTION_PREDICATE_OFFSET = 970u;
const uint SIMPLE_DDGI_SCHEDULER_FEEDBACK_MISSING_COMPLETION_OFFSET = 971u;
const uint SIMPLE_DDGI_SCHEDULER_FEEDBACK_PRODUCER_FAILURE_OFFSET = 972u;
const uint SIMPLE_DDGI_SCHEDULER_FEEDBACK_CACHE_READ_FAILURE_OFFSET = 973u;
const uint SIMPLE_DDGI_SCHEDULER_FEEDBACK_ACTIVE_SOURCE_MUTATION_OFFSET = 974u;
const uint SIMPLE_DDGI_SCHEDULER_FEEDBACK_ACTIVE_CANONICAL_MUTATION_OFFSET = 975u;
const uint SIMPLE_DDGI_SCHEDULER_FEEDBACK_ELIGIBLE_CLASS_OFFSET = 976u;
const uint SIMPLE_DDGI_SCHEDULER_FEEDBACK_ELIGIBLE_RING_OFFSET = 983u;
const uint SIMPLE_DDGI_SCHEDULER_FEEDBACK_RESIDUAL_PROPAGATION_OFFSET = 986u;
const uint SIMPLE_DDGI_SCHEDULER_FEEDBACK_URGENT_RELIGHT_OFFSET = 990u;
const uint SIMPLE_DDGI_SCHEDULER_FEEDBACK_RECEIVER_CONTRIBUTION_OFFSET = 994u;
const uint SIMPLE_DDGI_SCHEDULER_FEEDBACK_TRANSPORT_TOPOLOGY_OFFSET = 998u;
const uint SIMPLE_DDGI_SCHEDULER_FEEDBACK_ADAPTIVE_SAVED_RING_OFFSET = 999u;
const uint SIMPLE_DDGI_SCHEDULER_FEEDBACK_ADAPTIVE_ERROR_RING_OFFSET = 1002u;
const uint SIMPLE_DDGI_SCHEDULER_FEEDBACK_ADAPTIVE_SAVED_CONTENT_OFFSET = 1005u;
const uint SIMPLE_DDGI_SCHEDULER_FEEDBACK_ADAPTIVE_ERROR_CONTENT_OFFSET = 1009u;

const uint SIMPLE_DDGI_SCHEDULER_DISPATCH_RESET = 0u;
const uint SIMPLE_DDGI_SCHEDULER_DISPATCH_CLASSIFY = 1u;
const uint SIMPLE_DDGI_SCHEDULER_DISPATCH_PREFIX = 2u;
const uint SIMPLE_DDGI_SCHEDULER_DISPATCH_LANE_BASE = 3u;
const uint SIMPLE_DDGI_SCHEDULER_DISPATCH_COMPACT = 4u;
const uint SIMPLE_DDGI_SCHEDULER_DISPATCH_ADMIT = 5u;
const uint SIMPLE_DDGI_SCHEDULER_DISPATCH_EMIT = 6u;
const uint SIMPLE_DDGI_SCHEDULER_DISPATCH_COMMIT_LOCAL = 7u;
const uint SIMPLE_DDGI_SCHEDULER_DISPATCH_COMMIT_PROPAGATION = 8u;
const uint SIMPLE_DDGI_SCHEDULER_DISPATCH_FEEDBACK = 9u;
const uint SIMPLE_DDGI_SCHEDULER_DISPATCH_TRACE = 10u;
const uint SIMPLE_DDGI_SCHEDULER_DISPATCH_RELOCATE = 11u;
const uint SIMPLE_DDGI_SCHEDULER_DISPATCH_TRANSPORT = 12u;
const uint SIMPLE_DDGI_SCHEDULER_DISPATCH_BLEND = 13u;
const uint SIMPLE_DDGI_SCHEDULER_DISPATCH_PUBLISH = 14u;

layout(push_constant) uniform SimpleDdgiScheduleBlock
{
    uint ArenaBufferIndex;
    uint ParamsBufferIndex;
    uint ProbeStateBufferIndex;
    uint UpdateQueueBufferIndex;
    uint RelocationBufferIndex;
    uint FrameOffsetWords;
    uint VolumePolicyOffsetWords;
    uint PreviousVolumePolicyOffsetWords;
    uint DirtyRegionOffsetWords;
    uint SchedulerProbeStateOffsetWords;
    uint CandidateInputOffsetWords;
    uint CandidateGroupLaneCountsOffsetWords;
    uint CandidateOutputOffsetWords;
    uint LaneCandidateCountsOffsetWords;
    uint LanePrefixesOffsetWords;
    uint LaneTotalsOffsetWords;
    uint LaneCursorsOffsetWords;
    uint LaneAdmissionOffsetWords;
    uint CountersOffsetWords;
    uint UpdateRecordsOffsetWords;
    uint RayBucketCommandsOffsetWords;
    uint RayBucketMetadataOffsetWords;
    uint IndirectCommandsOffsetWords;
    uint OutcomesOffsetWords;
    uint FeedbackOffsetWords;
    uint IrradianceAtlasBufferIndex;
    uint VisibilityAtlasBufferIndex;
    uint TransportIrradianceAtlasBufferIndex;
    uint PrivateVisibilityAtlasOffsetWords;
    uint Stage;
    uint ReceiverProbeBufferIndex;
} pc;

uint SchedulerArenaRead(uint wordOffset)
{
    return ReadStorageWordUniform(pc.ArenaBufferIndex, wordOffset);
}

void SchedulerArenaWrite(uint wordOffset, uint value)
{
    WriteStorageWordUniform(pc.ArenaBufferIndex, wordOffset, value);
}

uint SchedulerArenaAtomicAdd(uint wordOffset, uint value)
{
    return atomicAdd(BindlessStorageBuffers[nonuniformEXT(pc.ArenaBufferIndex)].Words[wordOffset], value);
}

uint SchedulerArenaAtomicOr(uint wordOffset, uint value)
{
    return atomicOr(BindlessStorageBuffers[nonuniformEXT(pc.ArenaBufferIndex)].Words[wordOffset], value);
}

uint SchedulerActiveProbeCount();
uint SchedulerFrameIndex();

bool SchedulerReadLegacyReceiverContribution(
    uint probeIndex,
    out float contribution,
    out uint coverageAndFlags)
{
    contribution = 0.0;
    coverageAndFlags = 0u;
    uint offsetWordsPlusOne = ReadStorageWordUniform(
        pc.ParamsBufferIndex,
        SIMPLE_DDGI_SCHEDULER_RECEIVER_OFFSET_PARAM_WORD);
    uint activeProbeCount = SchedulerActiveProbeCount();
    if (offsetWordsPlusOne == 0u || probeIndex >= activeProbeCount)
        return false;

    uint bankWords = activeProbeCount *
        SIMPLE_DDGI_SCHEDULER_RECEIVER_WORDS_PER_PROBE;
    uint previousBank = (SchedulerFrameIndex() - 1u) & 1u;
    uint recordBase = offsetWordsPlusOne - 1u +
        previousBank * bankWords +
        probeIndex * SIMPLE_DDGI_SCHEDULER_RECEIVER_WORDS_PER_PROBE;
    uint accumulatedWeightQ8 = SchedulerArenaRead(recordBase + 0u);
    coverageAndFlags = SchedulerArenaRead(recordBase + 1u);
    uint coveredBuckets = uint(bitCount(
        coverageAndFlags &
            SIMPLE_DDGI_SCHEDULER_RECEIVER_COVERAGE_MASK));
    float accumulatedWeight = float(accumulatedWeightQ8) *
        SIMPLE_DDGI_SCHEDULER_RECEIVER_WEIGHT_SCALE;
    float coverage = float(coveredBuckets) / 24.0;
    // Logarithmic mass prevents resolution and transparent overdraw from
    // dominating the bounded score. Coverage preserves a broad doorway/fog
    // signal even when each individual interpolation corner is low weight.
    contribution = clamp(
        log2(1.0 + max(accumulatedWeight, 0.0)) * 0.75 +
            coverage * 2.0,
        0.0,
        12.0);
    if ((coverageAndFlags &
            SIMPLE_DDGI_SCHEDULER_RECEIVER_FALLBACK_MASK) != 0u)
    {
        contribution = min(contribution + 0.25, 12.0);
    }
    if ((coverageAndFlags &
            SIMPLE_DDGI_SCHEDULER_RECEIVER_REFLECTION) != 0u)
    {
        contribution = min(contribution + 0.5, 12.0);
    }
    return true;
}

uint SchedulerFrame(uint word)
{
    return SchedulerArenaRead(pc.FrameOffsetWords + word);
}

uint SchedulerActiveProbeCount() { return SchedulerFrame(0u); }
uint SchedulerActiveVolumeCount() { return SchedulerFrame(1u); }
uint SchedulerCandidateCapacity() { return SchedulerFrame(2u); }
uint SchedulerCandidateGroupCount() { return (SchedulerCandidateCapacity() + 63u) / 64u; }
uint SchedulerRequestCapacity() { return SchedulerFrame(3u); }
uint SchedulerUrgentRelightBudget()
{
    return min(
        SchedulerArenaRead(
            pc.CountersOffsetWords +
                SIMPLE_DDGI_SCHEDULER_COUNTER_URGENT_CONTROL),
        255u);
}
bool SchedulerUrgentRelight()
{
    return SchedulerUrgentRelightBudget() != 0u;
}
uint SchedulerRequestBudget()
{
    uint ordinaryBudget = SchedulerFrame(5u);
    uint urgentBudget = SchedulerUrgentRelightBudget();
    return urgentBudget == 0u
        ? ordinaryBudget
        : min(ordinaryBudget, urgentBudget);
}
uint SchedulerPrimaryRayBudget() { return SchedulerFrame(6u); }
uint SchedulerSourceTargetProbes() { return SchedulerFrame(7u); }
uint SchedulerFrameIndex() { return SchedulerFrame(10u); }
uint SchedulerPeriodicSourceRefreshControlFrame() { return SchedulerFrame(11u); }
uint SchedulerVolumeGeneration() { return SchedulerFrame(14u); }
uint SchedulerResourceGeneration() { return SchedulerFrame(15u); }
uint SchedulerQueueGeneration() { return SchedulerFrame(16u); }
uint SchedulerSourceGeneration() { return SchedulerFrame(17u); }
uint SchedulerTransportGeneration() { return SchedulerFrame(18u); }
uint SchedulerTransportTopologyGeneration() { return SchedulerFrame(55u); }
uint SchedulerDirtyRegionCount() { return SchedulerFrame(24u); }
uint SchedulerDirtyReasonFlags() { return SchedulerFrame(26u); }
uint SchedulerSourceTargetRays() { return SchedulerFrame(8u); }
uint SchedulerFeatureFlags() { return SchedulerFrame(27u); }
uint SchedulerSourceRefreshMode()
{
    uint mode = SchedulerFrame(31u);
    return mode <= 3u
        ? mode
        : SIMPLE_DDGI_SCHEDULER_SOURCE_MODE_FULL_TRACE;
}

bool SchedulerSourceModeUsesRadiometricRelight(uint mode)
{
    return mode == SIMPLE_DDGI_SCHEDULER_SOURCE_MODE_ENVIRONMENT_RELIGHT ||
        mode == SIMPLE_DDGI_SCHEDULER_SOURCE_MODE_CACHED_HIT_RELIGHT;
}

bool SchedulerCandidateUsesRadiometricRelight(uint candidateReasons)
{
    return SchedulerSourceModeUsesRadiometricRelight(
            SchedulerSourceRefreshMode()) &&
        (candidateReasons &
            SIMPLE_DDGI_SCHEDULER_REASON_RADIOMETRIC_RELIGHT) != 0u;
}

bool SchedulerUpdateUsesRadiometricRelight(uint updateFlags)
{
    return (updateFlags & SIMPLE_DDGI_SCHEDULER_UPDATE_SOURCE_REFRESH) != 0u &&
        SchedulerSourceModeUsesRadiometricRelight(
            (updateFlags & SIMPLE_DDGI_SCHEDULER_UPDATE_SOURCE_MODE_MASK) >>
                SIMPLE_DDGI_SCHEDULER_UPDATE_SOURCE_MODE_SHIFT);
}

bool SchedulerUpdatePreservesSourceEpoch(uint updateFlags)
{
    if ((updateFlags & SIMPLE_DDGI_SCHEDULER_UPDATE_SOURCE_REFRESH) == 0u)
        return true;
    uint mode = (updateFlags &
        SIMPLE_DDGI_SCHEDULER_UPDATE_SOURCE_MODE_MASK) >>
            SIMPLE_DDGI_SCHEDULER_UPDATE_SOURCE_MODE_SHIFT;
    return SchedulerSourceModeUsesRadiometricRelight(mode) ||
        mode == SIMPLE_DDGI_SCHEDULER_SOURCE_MODE_SEGMENT_SELECTIVE;
}

bool SchedulerCandidateUsesEnvironmentRelight(uint candidateReasons)
{
    return SchedulerSourceRefreshMode() ==
            SIMPLE_DDGI_SCHEDULER_SOURCE_MODE_ENVIRONMENT_RELIGHT &&
        SchedulerCandidateUsesRadiometricRelight(candidateReasons);
}

bool SchedulerUpdateUsesEnvironmentRelight(uint updateFlags)
{
    return ((updateFlags & SIMPLE_DDGI_SCHEDULER_UPDATE_SOURCE_MODE_MASK) >>
            SIMPLE_DDGI_SCHEDULER_UPDATE_SOURCE_MODE_SHIFT) ==
                SIMPLE_DDGI_SCHEDULER_SOURCE_MODE_ENVIRONMENT_RELIGHT &&
        SchedulerUpdateUsesRadiometricRelight(updateFlags);
}

uint SchedulerAdvanceSourceEpoch(uint epoch)
{
    uint next = epoch + 1u;
    return next == 0u ? 1u : next;
}
uint SchedulerInvalidationMarker() { return SchedulerFrame(38u); }
bool SchedulerGpuResident() { return (SchedulerFeatureFlags() & 1u) != 0u; }
bool SchedulerGpuMirror() { return (SchedulerFeatureFlags() & 2u) != 0u; }
bool SchedulerTransportV2() { return (SchedulerFeatureFlags() & 4u) != 0u; }
bool SchedulerToroidal() { return (SchedulerFeatureFlags() & 8u) != 0u; }
bool SchedulerAtlasFresh() { return (SchedulerFeatureFlags() & 16u) != 0u; }
bool SchedulerGlobalConvergence() { return (SchedulerFeatureFlags() & 32u) != 0u; }
bool SchedulerDirtyOverflow() { return (SchedulerFeatureFlags() & 64u) != 0u; }
bool SchedulerClassificationEnabled() { return (SchedulerFeatureFlags() & 128u) != 0u; }
bool SchedulerSampledPublicationRequired() {
    return (SchedulerFeatureFlags() & SIMPLE_DDGI_SCHEDULER_FEATURE_SAMPLED_PUBLICATION) != 0u;
}
bool SchedulerTailCertification() {
    return (SchedulerFeatureFlags() & SIMPLE_DDGI_SCHEDULER_FEATURE_TAIL_CERTIFICATION) != 0u;
}
bool SchedulerPeriodicSourceRefreshWave() {
    return (SchedulerFeatureFlags() &
        SIMPLE_DDGI_SCHEDULER_FEATURE_PERIODIC_SOURCE_WAVE) != 0u;
}
bool SchedulerCertifiedQuiesced() {
    return (SchedulerFeatureFlags() &
        SIMPLE_DDGI_SCHEDULER_FEATURE_CERTIFIED_QUIESCED) != 0u;
}
bool SchedulerCostAwarePriority() {
    return (SchedulerFeatureFlags() &
        SIMPLE_DDGI_SCHEDULER_FEATURE_COST_AWARE_PRIORITY) != 0u;
}
bool SchedulerSparseResidualPropagation() {
    return (SchedulerFeatureFlags() &
        SIMPLE_DDGI_SCHEDULER_FEATURE_SPARSE_RESIDUAL_PROPAGATION) != 0u;
}
bool SchedulerExactReceiverFeedback() {
    return (SchedulerFeatureFlags() &
        SIMPLE_DDGI_SCHEDULER_FEATURE_EXACT_RECEIVER_FEEDBACK) != 0u &&
        (SchedulerFrame(
            SIMPLE_DDGI_SCHEDULER_FRAME_EXACT_FEEDBACK_FLAGS) &
            SIMPLE_DDGI_SCHEDULER_EXACT_FEEDBACK_BINDING_VALID) != 0u;
}
bool SchedulerAdaptiveRayCardinality() {
    return (SchedulerFeatureFlags() &
        SIMPLE_DDGI_SCHEDULER_FEATURE_ADAPTIVE_RAY_CARDINALITY) != 0u;
}
bool SchedulerResetLaneCursors() {
    return (SchedulerFeatureFlags() &
        SIMPLE_DDGI_SCHEDULER_FEATURE_RESET_LANE_CURSORS) != 0u;
}
bool SchedulerDefersRadiometricPublication() {
    return (SchedulerFeatureFlags() &
        SIMPLE_DDGI_SCHEDULER_FEATURE_DEFER_RADIOMETRIC_PUBLICATION) != 0u;
}

shared uint schedulerExactFeedbackHeaderValid;
shared uint schedulerExactFeedbackSummaryCount;

// Called exactly once near the top of classify main, before any per-probe
// early return. One lane validates the complete immutable bank header and the
// workgroup shares the result; no hot path rereads 16 header words per probe.
void SchedulerPrepareReceiverFeedbackWorkgroup()
{
    if (gl_LocalInvocationIndex == 0u)
    {
        schedulerExactFeedbackHeaderValid = 0u;
        schedulerExactFeedbackSummaryCount = 0u;
        if (SchedulerExactReceiverFeedback())
        {
            uint summaryCount;
            bool valid = SimpleDdgiFeedbackSummaryTryValidateHeader(
                SchedulerFrame(
                    SIMPLE_DDGI_SCHEDULER_FRAME_EXACT_FEEDBACK_BUFFER),
                SchedulerFrame(
                    SIMPLE_DDGI_SCHEDULER_FRAME_EXACT_FEEDBACK_BANK_OFFSET),
                SchedulerFrame(
                    SIMPLE_DDGI_SCHEDULER_FRAME_EXACT_FEEDBACK_BANK_STRIDE),
                SchedulerFrame(
                    SIMPLE_DDGI_SCHEDULER_FRAME_EXACT_FEEDBACK_RECORD_CAPACITY),
                SchedulerFrame(
                    SIMPLE_DDGI_SCHEDULER_FRAME_EXACT_FEEDBACK_SUMMARY_CAPACITY),
                SchedulerFrame(
                    SIMPLE_DDGI_SCHEDULER_FRAME_EXACT_FEEDBACK_FALLBACK_CAPACITY),
                SchedulerFrame(
                    SIMPLE_DDGI_SCHEDULER_FRAME_EXACT_FEEDBACK_GENERATION),
                SchedulerFrame(
                    SIMPLE_DDGI_SCHEDULER_FRAME_EXACT_FEEDBACK_VIEWPORT),
                SchedulerFrame(
                    SIMPLE_DDGI_SCHEDULER_FRAME_EXACT_FEEDBACK_FRAME_LOW),
                SchedulerFrame(
                    SIMPLE_DDGI_SCHEDULER_FRAME_EXACT_FEEDBACK_FRAME_HIGH),
                summaryCount);
            schedulerExactFeedbackHeaderValid = valid ? 1u : 0u;
            schedulerExactFeedbackSummaryCount = valid ? summaryCount : 0u;
        }
    }
    barrier();
}

bool SchedulerReadExactReceiverContribution(
    uint probeIndex,
    out float contribution,
    out uint coverageAndFlags)
{
    contribution = 0.0;
    coverageAndFlags = 0u;
    if (schedulerExactFeedbackHeaderValid == 0u)
        return false;

    return SimpleDdgiFeedbackSummaryTryFindProbe(
        SchedulerFrame(
            SIMPLE_DDGI_SCHEDULER_FRAME_EXACT_FEEDBACK_BUFFER),
        SchedulerFrame(
            SIMPLE_DDGI_SCHEDULER_FRAME_EXACT_FEEDBACK_BANK_OFFSET),
        SchedulerFrame(
            SIMPLE_DDGI_SCHEDULER_FRAME_EXACT_FEEDBACK_SUMMARY_CAPACITY),
        schedulerExactFeedbackSummaryCount,
        SchedulerFrame(
            SIMPLE_DDGI_SCHEDULER_FRAME_EXACT_FEEDBACK_GENERATION),
        probeIndex,
        contribution,
        coverageAndFlags);
}

bool SchedulerReadPreviousReceiverContribution(
    uint probeIndex,
    out float contribution,
    out uint coverageAndFlags)
{
    // Exact mode never falls through to the rejected packed sketch. A stale or
    // malformed V2 bank contributes zero and ordinary scheduler priorities
    // remain authoritative for this frame.
    if (SchedulerExactReceiverFeedback())
    {
        return SchedulerReadExactReceiverContribution(
            probeIndex,
            contribution,
            coverageAndFlags);
    }
    return SchedulerReadLegacyReceiverContribution(
        probeIndex,
        contribution,
        coverageAndFlags);
}
uint SchedulerSolveEpoch() { return SchedulerFrame(19u); }

float SchedulerDecodeCostFrequency(uint frameWord)
{
    return float(min(SchedulerFrame(frameWord), 0xffffu)) / 256.0;
}

float SchedulerPredictedRelativeCost(
    uint rayCount,
    bool primaryTrace,
    bool cachedOnly)
{
    float rays = float(max(rayCount, 1u));
    if (cachedOnly)
        return rays * 0.15;
    if (!primaryTrace)
        return rays * 0.35;
    float visibility = SchedulerDecodeCostFrequency(40u);
    float alphaCandidates = SchedulerDecodeCostFrequency(41u);
    float materialEvaluations = SchedulerDecodeCostFrequency(42u);
    float farFieldSteps = SchedulerDecodeCostFrequency(43u);
    float perRay = 1.0 +
        visibility * 0.75 +
        alphaCandidates * 0.50 +
        materialEvaluations * 0.25 +
        farFieldSteps * 0.0625;
    return rays * max(perRay, 0.01);
}

float SchedulerErrorReductionPerCost(
    float residual,
    float receiverContribution,
    uint age,
    uint maximumLatency,
    float predictedCost)
{
    float safeResidual = isnan(residual) || isinf(residual)
        ? 1.0
        : max(residual, 0.0);
    float safeContribution = isnan(receiverContribution) ||
            isinf(receiverContribution)
        ? 0.0
        : clamp(receiverContribution, 0.0, 16.0);
    float normalizedAge = clamp(
        float(age) / float(max(maximumLatency, 1u)),
        0.0,
        4.0);
    float deadlineDebt = normalizedAge * normalizedAge;
    float benefit = max(safeResidual, 0.0001) *
        (1.0 + safeContribution) + deadlineDebt;
    return benefit / max(predictedCost, 0.0001);
}

bool SchedulerFrameReached(uint frame, uint target)
{
    return target == 0u || frame - target < 0x80000000u;
}

bool SchedulerSourceRefreshDueAtCutoff(
    uint lastSourceFrame,
    uint cutoffFrame,
    uint refreshInterval)
{
    uint ageAtCutoff = cutoffFrame - lastSourceFrame;
    return ageAtCutoff < 0x80000000u &&
        ageAtCutoff >= max(refreshInterval, 1u);
}

uint SchedulerLaneIndex(
    uint volume,
    uint workClass,
    uint transport,
    uint rayTier)
{
    return (((volume * SIMPLE_DDGI_SCHEDULER_WORK_CLASSES + workClass) *
        SIMPLE_DDGI_SCHEDULER_TRANSPORT_CATEGORIES + transport) *
        SIMPLE_DDGI_SCHEDULER_RAY_TIERS) + rayTier;
}

void SchedulerDecodeLane(
    uint lane,
    out uint volume,
    out uint workClass,
    out uint transport,
    out uint rayTier)
{
    rayTier = lane & 1u;
    uint remainder = lane >> 1u;
    transport = remainder & 3u;
    remainder >>= 2u;
    workClass = remainder % SIMPLE_DDGI_SCHEDULER_WORK_CLASSES;
    volume = remainder / SIMPLE_DDGI_SCHEDULER_WORK_CLASSES;
}

uint SchedulerCandidateWord(uint base, uint word)
{
    return SchedulerArenaRead(base + word);
}

void SchedulerWriteCandidate(
    uint base,
    uint probeIndex,
    uint volumeIndex,
    uint workClassAndTransport,
    uint rayTierAndReason)
{
    SchedulerArenaWrite(base + 0u, probeIndex);
    SchedulerArenaWrite(base + 1u, volumeIndex);
    SchedulerArenaWrite(base + 2u, workClassAndTransport);
    SchedulerArenaWrite(base + 3u, rayTierAndReason);
}

void SchedulerWriteInvalidCandidate(uint base)
{
    // Compact and admission gate on word zero, so invalidation is one store.
    SchedulerArenaWrite(base, SIMPLE_DDGI_SCHEDULER_INVALID_PROBE);
}

// Public state stores the age already accumulated by a transaction that must
// stay pending (notably relocation). The private resident word records the
// frame of the last successful commit. Combining both gives GPU ownership the
// same wall-clock retry age as the CPU scheduler without a full-field age pass.
uint SchedulerProbeUpdateAge(
    uint persistedAge,
    uint lastCommittedUpdateFrame)
{
    uint elapsed = min(
        SchedulerFrameIndex() - lastCommittedUpdateFrame,
        255u);
    return min(min(persistedAge, 255u) + elapsed, 255u);
}

void SchedulerReadCandidate(
    uint base,
    out uint probeIndex,
    out uint volumeIndex,
    out uint workClassAndTransport,
    out uint rayTierAndReason)
{
    probeIndex = SchedulerCandidateWord(base, 0u);
    volumeIndex = SchedulerCandidateWord(base, 1u);
    workClassAndTransport = SchedulerCandidateWord(base, 2u);
    rayTierAndReason = SchedulerCandidateWord(base, 3u);
}

uint SchedulerVolumeWord(uint volumeIndex, uint word)
{
    return SchedulerArenaRead(pc.VolumePolicyOffsetWords +
        volumeIndex * SIMPLE_DDGI_SCHEDULER_VOLUME_POLICY_WORDS + word);
}

uint SchedulerVolumeFirstProbe(uint volumeIndex) { return SchedulerVolumeWord(volumeIndex, 0u); }
uint SchedulerVolumeProbeCount(uint volumeIndex) { return SchedulerVolumeWord(volumeIndex, 1u); }
uint SchedulerVolumeKind(uint volumeIndex) { return SchedulerVolumeWord(volumeIndex, 2u); }
uint SchedulerVolumeRing(uint volumeIndex) { return SchedulerVolumeWord(volumeIndex, 3u); }
uint SchedulerVolumeSourceOrdinal(uint volumeIndex) { return SchedulerVolumeWord(volumeIndex, 4u); }
uint SchedulerVolumeFullRays(uint volumeIndex) { return SchedulerVolumeWord(volumeIndex, 30u); }
uint SchedulerVolumeMaintenanceRays(uint volumeIndex) { return SchedulerVolumeWord(volumeIndex, 31u); }

uint SchedulerAdaptiveMinimumSourceRays(uint volumeIndex)
{
    uint maximum = clamp(
        SchedulerVolumeFullRays(volumeIndex),
        1u,
        SIMPLE_DDGI_SCHEDULER_MAX_RAYS);
    return clamp(SchedulerVolumeMaintenanceRays(volumeIndex), 1u, maximum);
}

uint SchedulerAdaptiveMaximumSourceRays(uint volumeIndex)
{
    return clamp(
        SchedulerVolumeFullRays(volumeIndex),
        1u,
        SIMPLE_DDGI_SCHEDULER_MAX_RAYS);
}

uint SchedulerAdaptivePromoteSourceRays(uint volumeIndex, uint current)
{
    uint maximum = SchedulerAdaptiveMaximumSourceRays(volumeIndex);
    uint result = maximum;
    for (uint bucket = 0u; bucket < 6u; bucket++)
    {
        uint candidate = SchedulerFrame(32u + bucket);
        if (candidate > current && candidate <= maximum)
            result = min(result, candidate);
    }
    return result;
}

uint SchedulerAdaptiveDemoteSourceRays(uint volumeIndex, uint current)
{
    uint minimum = SchedulerAdaptiveMinimumSourceRays(volumeIndex);
    uint result = minimum;
    for (uint bucket = 0u; bucket < 6u; bucket++)
    {
        uint candidate = SchedulerFrame(32u + bucket);
        if (candidate >= minimum && candidate < current)
            result = max(result, candidate);
    }
    return result;
}

uint SchedulerAdaptiveBaselineSourceRays(uint volumeIndex)
{
    // Solver residual is not a directional-variance estimate. A short fixed
    // prefix can look converged while retaining visible spatial estimator
    // noise, so use the authored full count until variance evidence exists.
    return SchedulerAdaptiveMaximumSourceRays(volumeIndex);
}

bool SchedulerReadQuadratureWitness(
    uint probeIndex,
    out float quadratureError)
{
    quadratureError = 1.0;
    // During a frozen tail audit this word carries the exact solve epoch. Ray
    // cardinality is immutable until the audit completes or is invalidated.
    if (SchedulerTailCertification())
        return false;

    uint stateBase = pc.SchedulerProbeStateOffsetWords +
        probeIndex * SIMPLE_DDGI_SCHEDULER_PROBE_STATE_WORDS;
    uint packed = SchedulerArenaRead(stateBase + 9u);
    if ((packed & SIMPLE_DDGI_SCHEDULER_QUADRATURE_WITNESS_VALID) == 0u)
        return false;
    quadratureError = uintBitsToFloat(
        packed & ~SIMPLE_DDGI_SCHEDULER_QUADRATURE_WITNESS_VALID);
    return !isnan(quadratureError) && !isinf(quadratureError) &&
        quadratureError >= 0.0 && quadratureError <= 1.0;
}

bool SchedulerAdaptivePromotionDue(
    uint probeIndex,
    uint volumeIndex,
    uint sourceRayCount,
    float residual,
    uint stableUpdates)
{
    if (sourceRayCount == 0u)
        return false;
    uint maximum = SchedulerAdaptiveMaximumSourceRays(volumeIndex);
    if (sourceRayCount >= maximum || SchedulerTailCertification())
        return false;
    // Turning adaptation off must repair any previously committed short
    // sequence instead of leaving its noisy estimate frozen indefinitely.
    if (!SchedulerAdaptiveRayCardinality())
        return true;
    float quadratureError;
    if (!SchedulerReadQuadratureWitness(probeIndex, quadratureError) ||
        quadratureError > SIMPLE_DDGI_SCHEDULER_QUADRATURE_ERROR_THRESHOLD)
        return true;
    float tolerance = max(uintBitsToFloat(SchedulerFrame(39u)), 1.0e-7);
    return stableUpdates != 0u && !isnan(residual) && !isinf(residual) &&
        residual > tolerance * 2.0;
}

uint SchedulerResolveAdaptiveSourceRayCount(
    uint probeIndex,
    uint volumeIndex,
    uint candidateReasons,
    bool sourceWork)
{
    uint maximum = SchedulerAdaptiveMaximumSourceRays(volumeIndex);
    if (!SchedulerTransportV2() || !SchedulerAdaptiveRayCardinality())
        return maximum;

    uint privateBase = pc.SchedulerProbeStateOffsetWords +
        probeIndex * SIMPLE_DDGI_SCHEDULER_PROBE_STATE_WORDS;
    uint packedLifecycle = SchedulerArenaRead(privateBase + 7u);
    uint current = packedLifecycle & SIMPLE_DDGI_SCHEDULER_SOURCE_RAY_MASK;
    if (current == 0u)
        return SchedulerAdaptiveBaselineSourceRays(volumeIndex);
    if (!sourceWork)
        return min(current, maximum);

    // Visibility, geometry, topology, and source-cache repairs rebuild the
    // complete directional sequence immediately. Radiometric cache relights
    // are handled below because their immutable cache owns only the currently
    // committed prefix and must issue no additional primary rays.
    if ((candidateReasons & (
            SIMPLE_DDGI_SCHEDULER_REASON_GLOBAL_DIRTY |
            SIMPLE_DDGI_SCHEDULER_REASON_REGIONAL_DIRTY |
            SIMPLE_DDGI_SCHEDULER_REASON_SOURCE_INVALID |
            SIMPLE_DDGI_SCHEDULER_REASON_TOPOLOGY |
            SIMPLE_DDGI_SCHEDULER_REASON_RELOCATION_RETRY)) != 0u &&
        (candidateReasons &
            SIMPLE_DDGI_SCHEDULER_REASON_RADIOMETRIC_RELIGHT) == 0u)
    {
        return maximum;
    }

    // Relight transactions must re-evaluate the exact committed sequence;
    // topology/repair transactions preserve its size. Resizing is an explicit
    // routine source transaction selected by classify below.
    if ((candidateReasons & (
            SIMPLE_DDGI_SCHEDULER_REASON_RADIOMETRIC_RELIGHT |
            SIMPLE_DDGI_SCHEDULER_REASON_SEGMENT_SELECTIVE)) != 0u)
    {
        return min(current, maximum);
    }
    if ((candidateReasons &
            SIMPLE_DDGI_SCHEDULER_REASON_RESIDUAL_PROPAGATION) != 0u)
    {
        return SchedulerAdaptivePromoteSourceRays(volumeIndex, current);
    }

    if ((candidateReasons & SIMPLE_DDGI_SCHEDULER_REASON_ROUTINE_DUE) != 0u)
    {
        uint stableUpdates = (packedLifecycle &
            SIMPLE_DDGI_SCHEDULER_STABLE_UPDATE_MASK) >>
            SIMPLE_DDGI_SCHEDULER_STABLE_UPDATE_SHIFT;
        float residual = ReadStorageFloatUniform(
            pc.ProbeStateBufferIndex,
            probeIndex * SIMPLE_DDGI_PROBE_STATE_WORDS + 7u);
        float tolerance = max(uintBitsToFloat(SchedulerFrame(39u)), 1.0e-7);
        uint demotionRequirement = max(SchedulerFrame(30u) * 4u, 8u);
        float quadratureError;
        bool quadratureCertified = SchedulerReadQuadratureWitness(
            probeIndex,
            quadratureError) &&
            quadratureError <=
                SIMPLE_DDGI_SCHEDULER_QUADRATURE_ERROR_THRESHOLD;
        if (current > SchedulerAdaptiveMinimumSourceRays(volumeIndex) &&
            stableUpdates >= demotionRequirement && quadratureCertified &&
            !isnan(residual) && !isinf(residual) &&
            residual <= tolerance * 0.25)
        {
            return SchedulerAdaptiveDemoteSourceRays(volumeIndex, current);
        }
    }
    return min(current, maximum);
}
uint SchedulerVolumeLayoutGeneration(uint volumeIndex) { return SchedulerVolumeWord(volumeIndex, 6u); }
uint SchedulerVolumePreviousLayoutGeneration(uint volumeIndex) { return SchedulerVolumeWord(volumeIndex, 7u); }
uint SchedulerVolumeCurrentCountX(uint volumeIndex) { return SchedulerVolumeWord(volumeIndex, 16u); }
uint SchedulerVolumeCurrentCountY(uint volumeIndex) { return SchedulerVolumeWord(volumeIndex, 17u); }
uint SchedulerVolumeCurrentCountZ(uint volumeIndex) { return SchedulerVolumeWord(volumeIndex, 18u); }
uint SchedulerVolumePreviousCountX(uint volumeIndex) { return SchedulerVolumeWord(volumeIndex, 19u); }
uint SchedulerVolumePreviousCountY(uint volumeIndex) { return SchedulerVolumeWord(volumeIndex, 20u); }
uint SchedulerVolumePreviousCountZ(uint volumeIndex) { return SchedulerVolumeWord(volumeIndex, 21u); }
uint SchedulerVolumePhysicalOffsetX(uint volumeIndex) { return SchedulerVolumeWord(volumeIndex, 22u); }
uint SchedulerVolumePhysicalOffsetY(uint volumeIndex) { return SchedulerVolumeWord(volumeIndex, 23u); }
uint SchedulerVolumePhysicalOffsetZ(uint volumeIndex) { return SchedulerVolumeWord(volumeIndex, 24u); }
uint SchedulerVolumeLayoutFlags(uint volumeIndex) { return SchedulerVolumeWord(volumeIndex, 25u); }
uint SchedulerVolumeSequenceStride(uint volumeIndex) { return SchedulerVolumeWord(volumeIndex, 34u); }
uint SchedulerVolumeCacheBaseWord(uint volumeIndex) { return SchedulerVolumeWord(volumeIndex, 35u); }
uint SchedulerVolumeDirtyGeneration(uint volumeIndex) { return SchedulerVolumeWord(volumeIndex, 39u); }
float SchedulerVolumeProximityRadiusPadding(uint volumeIndex)
{
    return uintBitsToFloat(SchedulerVolumeWord(volumeIndex, 40u));
}
uint SchedulerVolumeCacheWordsPerProbe(uint volumeIndex) { return SchedulerVolumeWord(volumeIndex, 41u); }
uint SchedulerVolumeCachePhysicalFirstProbe(uint volumeIndex) { return SchedulerVolumeWord(volumeIndex, 42u) & 0xffffu; }
uint SchedulerVolumeCachePhysicalProbeCount(uint volumeIndex) { return SchedulerVolumeWord(volumeIndex, 42u) >> 16u; }
uint SchedulerVolumeCacheLayoutFlags(uint volumeIndex) { return SchedulerVolumeWord(volumeIndex, 43u); }

bool SchedulerVolumeUsesNearVisibilitySidecar(uint volumeIndex)
{
    uint kind = SchedulerVolumeKind(volumeIndex);
    return kind == SIMPLE_DDGI_SCHEDULER_VOLUME_KIND_REFINEMENT ||
        (kind == SIMPLE_DDGI_SCHEDULER_VOLUME_KIND_RING &&
            SchedulerVolumeSourceOrdinal(volumeIndex) == 10000u);
}

vec3 SchedulerVolumeOrigin(uint volumeIndex)
{
    return vec3(
        uintBitsToFloat(SchedulerVolumeWord(volumeIndex, 8u)),
        uintBitsToFloat(SchedulerVolumeWord(volumeIndex, 9u)),
        uintBitsToFloat(SchedulerVolumeWord(volumeIndex, 10u)));
}

float SchedulerVolumeSpacing(uint volumeIndex)
{
    return max(uintBitsToFloat(SchedulerVolumeWord(volumeIndex, 11u)), 0.0001);
}

bool SchedulerFindSequenceVolume(uint sequenceOrdinal, out uint volumeIndex, out uint localOrdinal)
{
    volumeIndex = SIMPLE_DDGI_SCHEDULER_INVALID_PROBE;
    localOrdinal = 0u;
    uint activeVolumes = min(SchedulerActiveVolumeCount(), SIMPLE_DDGI_SCHEDULER_MAX_VOLUMES);
    for (uint i = 0u; i < SIMPLE_DDGI_SCHEDULER_MAX_VOLUMES; i++)
    {
        if (i >= activeVolumes)
            continue;
        uint firstProbe = SchedulerVolumeFirstProbe(i);
        uint probeCount = SchedulerVolumeProbeCount(i);
        if (sequenceOrdinal >= firstProbe && sequenceOrdinal - firstProbe < probeCount)
        {
            volumeIndex = i;
            localOrdinal = sequenceOrdinal - firstProbe;
            return true;
        }
    }
    return false;
}

uint SchedulerSequenceProbeIndex(uint volumeIndex, uint localOrdinal)
{
    uint count = max(SchedulerVolumeProbeCount(volumeIndex), 1u);
    uint stride = max(SchedulerVolumeSequenceStride(volumeIndex), 1u);
    uint logical = (localOrdinal * stride) % count;
    uint countX = max(SchedulerVolumeCurrentCountX(volumeIndex), 1u);
    uint countY = max(SchedulerVolumeCurrentCountY(volumeIndex), 1u);
    uint countZ = max(SchedulerVolumeCurrentCountZ(volumeIndex), 1u);
    uint xy = countX * countY;
    uint z = logical / xy;
    uint rem = logical - z * xy;
    uint y = rem / countX;
    uint x = rem - y * countX;
    uint physicalX = (x + SchedulerVolumePhysicalOffsetX(volumeIndex)) % countX;
    uint physicalY = (y + SchedulerVolumePhysicalOffsetY(volumeIndex)) % countY;
    uint physicalZ = (z + SchedulerVolumePhysicalOffsetZ(volumeIndex)) % countZ;
    return SchedulerVolumeFirstProbe(volumeIndex) +
        physicalX + physicalY * countX + physicalZ * countX * countY;
}

bool SchedulerDirtyIntersects(uint volumeIndex, uint localOrdinal, out uint reasons)
{
    reasons = 0u;
    uint countX = max(SchedulerVolumeCurrentCountX(volumeIndex), 1u);
    uint countY = max(SchedulerVolumeCurrentCountY(volumeIndex), 1u);
    uint xy = countX * countY;
    uint z = localOrdinal / xy;
    uint rem = localOrdinal - z * xy;
    uint y = rem / countX;
    uint x = rem - y * countX;
    vec3 position = SchedulerVolumeOrigin(volumeIndex) +
        vec3(x, y, z) * SchedulerVolumeSpacing(volumeIndex);
    uint dirtyCount = min(SchedulerDirtyRegionCount(), SchedulerFrame(25u));
    float expand = SchedulerVolumeSpacing(volumeIndex);
    for (uint i = 0u; i < SIMPLE_DDGI_SCHEDULER_MAX_VOLUMES * 64u; i++)
    {
        if (i >= dirtyCount)
            continue;
        uint base = pc.DirtyRegionOffsetWords + i * 20u;
        vec3 minimum = vec3(
            uintBitsToFloat(SchedulerArenaRead(base + 0u)),
            uintBitsToFloat(SchedulerArenaRead(base + 1u)),
            uintBitsToFloat(SchedulerArenaRead(base + 2u))) - vec3(expand);
        vec3 maximum = vec3(
            uintBitsToFloat(SchedulerArenaRead(base + 4u)),
            uintBitsToFloat(SchedulerArenaRead(base + 5u)),
            uintBitsToFloat(SchedulerArenaRead(base + 6u))) + vec3(expand);
        if (all(greaterThanEqual(position, minimum)) && all(lessThanEqual(position, maximum)))
            reasons |= SchedulerArenaRead(base + 16u);
    }
    return reasons != 0u;
}

uint SchedulerGroupLaneCount(uint group, uint lane)
{
    uint logicalIndex = group * SIMPLE_DDGI_SCHEDULER_MAX_LANES + lane;
    uint packed = SchedulerArenaRead(pc.CandidateGroupLaneCountsOffsetWords +
        (logicalIndex >> 1u));
    return (packed >> ((logicalIndex & 1u) * 16u)) & 0xffffu;
}

void SchedulerAtomicIncrementGroupLaneCount(uint group, uint lane)
{
    uint logicalIndex = group * SIMPLE_DDGI_SCHEDULER_MAX_LANES + lane;
    uint wordOffset = pc.CandidateGroupLaneCountsOffsetWords + (logicalIndex >> 1u);
    uint shift = (logicalIndex & 1u) * 16u;
    uint mask = 0xffffu << shift;
    uint observed = atomicAdd(
        BindlessStorageBuffers[nonuniformEXT(pc.ArenaBufferIndex)].Words[wordOffset],
        0u);
    for (;;)
    {
        uint value = (observed >> shift) & 0xffffu;
        if (value == 0xffffu)
        {
            SchedulerArenaAtomicAdd(
                pc.CountersOffsetWords + SIMPLE_DDGI_SCHEDULER_COUNTER_OVERFLOW,
                1u);
            return;
        }
        uint replacement = (observed & ~mask) | ((value + 1u) << shift);
        uint previous = atomicCompSwap(
            BindlessStorageBuffers[nonuniformEXT(pc.ArenaBufferIndex)].Words[wordOffset],
            observed,
            replacement);
        if (previous == observed)
            return;
        observed = previous;
    }
}

bool SchedulerFindVolume(uint probeIndex, out uint volumeIndex)
{
    volumeIndex = SIMPLE_DDGI_SCHEDULER_INVALID_PROBE;
    uint activeVolumes = min(SchedulerActiveVolumeCount(), SIMPLE_DDGI_SCHEDULER_MAX_VOLUMES);
    for (uint i = 0u; i < SIMPLE_DDGI_SCHEDULER_MAX_VOLUMES; i++)
    {
        if (i >= activeVolumes)
            continue;
        uint firstProbe = SchedulerVolumeFirstProbe(i);
        uint probeCount = SchedulerVolumeProbeCount(i);
        if (probeIndex >= firstProbe && probeIndex - firstProbe < probeCount)
            volumeIndex = i;
    }
    return volumeIndex != SIMPLE_DDGI_SCHEDULER_INVALID_PROBE;
}

void SchedulerWriteIndirect(uint slot, uint groupCountX, uint groupCountY, uint groupCountZ)
{
    uint base = pc.IndirectCommandsOffsetWords + slot * SIMPLE_DDGI_SCHEDULER_INDIRECT_WORDS;
    SchedulerArenaWrite(base + 0u, groupCountX);
    SchedulerArenaWrite(base + 1u, groupCountY);
    SchedulerArenaWrite(base + 2u, groupCountZ);
    SchedulerArenaWrite(base + 3u, 0u);
}

uint SchedulerGroupsFor(uint count)
{
    return count == 0u ? 0u : (count + SIMPLE_DDGI_SCHEDULER_WORKGROUP_SIZE - 1u) /
        SIMPLE_DDGI_SCHEDULER_WORKGROUP_SIZE;
}

uint SchedulerCandidateGroupLaneWordCount()
{
    uint logicalCount = SchedulerCandidateGroupCount() * SIMPLE_DDGI_SCHEDULER_MAX_LANES;
    return (logicalCount + 1u) >> 1u;
}

bool SchedulerTryReserve(uint counterWord, uint amount, uint limit)
{
    if (amount == 0u)
        return true;
    uint oldValue = SchedulerArenaAtomicAdd(pc.CountersOffsetWords + counterWord, amount);
    if (oldValue <= limit && amount <= limit - oldValue)
        return true;
    SchedulerArenaAtomicAdd(pc.CountersOffsetWords + counterWord, 0u - amount);
    return false;
}

// Minimal sparse-address resolver for the scheduler ABI. Probe indices in the
// resident scheduler already identify toroidal physical slots, which are also
// the sparse page-table coordinate space. The complete receiver/update helper
// lives in ddgi_simple_page_shared.glsl.
bool SchedulerResolvePayloadAddressDetailed(
    uint probeIndex,
    uint volumeIndex,
    out uint physicalProbeIndex,
    out uint pageMappingGeneration,
    out bool published)
{
    const uint paramsHeaderWords = 64u;
    const uint volumeStrideWords = 28u;
    const uint maxVolumes = 16u;
    const uint pagingStrideWords = 8u;
    const uint pagingBaseWords =
        paramsHeaderWords + maxVolumes * volumeStrideWords;
    const uint pageTableOffsetWords = 144u;
    const uint pageTableEntryWords = 4u;
    const uint pageHistoryWords = 4u;
    const uint physicalMetadataWords = 12u;
    const uint sparseMode = 2u;
    const uint tableValid = 1u << 0u;
    const uint tableInitializing = 1u << 1u;
    const uint tablePublished = 1u << 2u;
    const uint tableSuppressed = 1u << 3u;
    const uint invalidIndex = 0xffffffffu;

    physicalProbeIndex = invalidIndex;
    pageMappingGeneration = 0u;
    published = false;
    if (volumeIndex >= SchedulerActiveVolumeCount())
        return false;

    uint pagingBase = pagingBaseWords +
        volumeIndex * pagingStrideWords;
    uint virtualFirst = ReadStorageWordUniform(
        pc.ParamsBufferIndex,
        pagingBase + 0u);
    uint pageTableFirst = ReadStorageWordUniform(
        pc.ParamsBufferIndex,
        pagingBase + 1u);
    uint densePhysicalFirst = ReadStorageWordUniform(
        pc.ParamsBufferIndex,
        pagingBase + 2u);
    uint mode = ReadStorageWordUniform(
        pc.ParamsBufferIndex,
        pagingBase + 3u);
    uint pageGridX = ReadStorageWordUniform(
        pc.ParamsBufferIndex,
        pagingBase + 4u);
    uint pageGridY = ReadStorageWordUniform(
        pc.ParamsBufferIndex,
        pagingBase + 5u);
    uint pageGridZ = ReadStorageWordUniform(
        pc.ParamsBufferIndex,
        pagingBase + 6u);
    uint sparsePoolFirst = ReadStorageWordUniform(
        pc.ParamsBufferIndex,
        pagingBase + 7u);
    uint physicalCapacity = ReadStorageWordUniform(
        pc.ParamsBufferIndex,
        54u);
    if (probeIndex < virtualFirst)
        return false;
    uint localProbe = probeIndex - virtualFirst;
    if (localProbe >= SchedulerVolumeProbeCount(volumeIndex))
        return false;

    if (mode != sparseMode)
    {
        physicalProbeIndex = densePhysicalFirst + localProbe;
        pageMappingGeneration = 0xffffffffu;
        published = physicalProbeIndex < physicalCapacity;
        return published;
    }

    uint residencyArena = ReadStorageWordUniform(
        pc.ParamsBufferIndex,
        52u);
    uint virtualPageCount = ReadStorageWordUniform(
        pc.ParamsBufferIndex,
        53u);
    uint resourceGeneration = ReadStorageWordUniform(
        pc.ParamsBufferIndex,
        55u);
    uint sparsePageCapacity = ReadStorageWordUniform(
        pc.ParamsBufferIndex,
        58u);
    uint residencyFlags = ReadStorageWordUniform(
        pc.ParamsBufferIndex,
        59u);
    if (residencyArena !=
            uint(SIMPLE_DDGI_RESIDENCY_ARENA_BUFFER_INDEX) ||
        resourceGeneration == 0u ||
        (residencyFlags & (1u << 0u)) == 0u ||
        (residencyFlags & (1u << 1u)) != 0u ||
        (residencyFlags & (1u << 2u)) == 0u ||
        ReadStorageWordUniform(residencyArena, 2u) !=
            resourceGeneration ||
        pageGridX == 0u || pageGridY == 0u || pageGridZ == 0u)
    {
        return false;
    }

    uint countX = max(SchedulerVolumeCurrentCountX(volumeIndex), 1u);
    uint countY = max(SchedulerVolumeCurrentCountY(volumeIndex), 1u);
    uint xy = countX * countY;
    uint physicalZ = localProbe / xy;
    uint remainder = localProbe - physicalZ * xy;
    uint physicalY = remainder / countX;
    uint physicalX = remainder - physicalY * countX;
    uint pageX = physicalX >> 1u;
    uint pageY = physicalY >> 1u;
    uint pageZ = physicalZ >> 1u;
    if (pageX >= pageGridX || pageY >= pageGridY ||
        pageZ >= pageGridZ)
    {
        return false;
    }
    uint virtualPage = pageTableFirst + pageX +
        pageY * pageGridX + pageZ * pageGridX * pageGridY;
    if (virtualPage >= virtualPageCount)
        return false;
    uint tableBase = pageTableOffsetWords +
        virtualPage * pageTableEntryWords;
    uint physicalPlusOne = ReadStorageWordUniform(
        residencyArena,
        tableBase + 0u);
    uint mappingGeneration = ReadStorageWordUniform(
        residencyArena,
        tableBase + 1u);
    uint tableFlags = ReadStorageWordUniform(
        residencyArena,
        tableBase + 2u);
    if (physicalPlusOne == 0u || mappingGeneration == 0u ||
        (tableFlags & tableValid) == 0u ||
        (tableFlags & tableSuppressed) != 0u)
    {
        return false;
    }
    uint physicalPage = physicalPlusOne - 1u;
    if (physicalPage >= sparsePageCapacity)
        return false;
    uint physicalMetadataOffset = pageTableOffsetWords +
        virtualPageCount *
            (pageTableEntryWords + pageHistoryWords);
    uint reverseBase = physicalMetadataOffset +
        physicalPage * physicalMetadataWords;
    if (ReadStorageWordUniform(residencyArena, reverseBase + 0u) !=
            virtualPage + 1u ||
        ReadStorageWordUniform(residencyArena, reverseBase + 1u) !=
            mappingGeneration ||
        ReadStorageWordUniform(residencyArena, reverseBase + 2u) !=
            resourceGeneration)
    {
        return false;
    }

    uint pageLocal = (physicalX & 1u) |
        ((physicalY & 1u) << 1u) |
        ((physicalZ & 1u) << 2u);
    physicalProbeIndex = sparsePoolFirst +
        physicalPage * 8u + pageLocal;
    pageMappingGeneration = mappingGeneration;
    published = (tableFlags & tablePublished) != 0u &&
        (tableFlags & tableInitializing) == 0u;
    return physicalProbeIndex < physicalCapacity;
}

bool SchedulerResolvePayloadAddress(
    uint probeIndex,
    uint volumeIndex,
    out uint physicalProbeIndex,
    out uint pageMappingGeneration)
{
    bool published;
    return SchedulerResolvePayloadAddressDetailed(
        probeIndex,
        volumeIndex,
        physicalProbeIndex,
        pageMappingGeneration,
        published);
}

bool SchedulerResolveSparsePageMetadata(
    uint probeIndex,
    uint volumeIndex,
    uint physicalProbeIndex,
    uint pageMappingGeneration,
    out uint residencyArena,
    out uint tableBase,
    out uint metadataBase,
    out uint pageLocalBit,
    out uint validProbeMask)
{
    const uint paramsHeaderWords = 64u;
    const uint volumeStrideWords = 28u;
    const uint maxVolumes = 16u;
    const uint pagingStrideWords = 8u;
    const uint pagingBaseWords = paramsHeaderWords +
        maxVolumes * volumeStrideWords;
    const uint pageTableOffsetWords = 144u;
    const uint pageTableEntryWords = 4u;
    const uint pageHistoryWords = 4u;
    const uint physicalMetadataWords = 12u;
    const uint sparseMode = 2u;

    residencyArena = 0u;
    tableBase = 0u;
    metadataBase = 0u;
    pageLocalBit = 0u;
    validProbeMask = 0u;
    if (volumeIndex >= SchedulerActiveVolumeCount())
        return false;
    uint pagingBase = pagingBaseWords + volumeIndex * pagingStrideWords;
    uint mode = ReadStorageWordUniform(
        pc.ParamsBufferIndex,
        pagingBase + 3u);
    if (mode != sparseMode)
        return false;
    uint virtualFirst = ReadStorageWordUniform(
        pc.ParamsBufferIndex,
        pagingBase + 0u);
    uint pageTableFirst = ReadStorageWordUniform(
        pc.ParamsBufferIndex,
        pagingBase + 1u);
    uint pageGridX = ReadStorageWordUniform(
        pc.ParamsBufferIndex,
        pagingBase + 4u);
    uint pageGridY = ReadStorageWordUniform(
        pc.ParamsBufferIndex,
        pagingBase + 5u);
    uint sparsePoolFirst = ReadStorageWordUniform(
        pc.ParamsBufferIndex,
        pagingBase + 7u);
    uint virtualPageCount = ReadStorageWordUniform(
        pc.ParamsBufferIndex,
        53u);
    uint sparsePageCapacity = ReadStorageWordUniform(
        pc.ParamsBufferIndex,
        58u);
    residencyArena = ReadStorageWordUniform(
        pc.ParamsBufferIndex,
        52u);
    if (residencyArena != uint(SIMPLE_DDGI_RESIDENCY_ARENA_BUFFER_INDEX) ||
        probeIndex < virtualFirst ||
        physicalProbeIndex < sparsePoolFirst ||
        pageMappingGeneration == 0u)
    {
        return false;
    }
    uint localProbe = probeIndex - virtualFirst;
    uint countX = max(SchedulerVolumeCurrentCountX(volumeIndex), 1u);
    uint countY = max(SchedulerVolumeCurrentCountY(volumeIndex), 1u);
    uint countZ = max(SchedulerVolumeCurrentCountZ(volumeIndex), 1u);
    uint xy = countX * countY;
    uint physicalZ = localProbe / xy;
    uint remainder = localProbe - physicalZ * xy;
    uint physicalY = remainder / countX;
    uint physicalX = remainder - physicalY * countX;
    uint pageX = physicalX >> 1u;
    uint pageY = physicalY >> 1u;
    uint pageZ = physicalZ >> 1u;
    uint virtualPage = pageTableFirst + pageX +
        pageY * pageGridX + pageZ * pageGridX * pageGridY;
    uint physicalPage = (physicalProbeIndex - sparsePoolFirst) / 8u;
    uint pageLocal = (physicalProbeIndex - sparsePoolFirst) & 7u;
    if (virtualPage >= virtualPageCount ||
        physicalPage >= sparsePageCapacity)
    {
        return false;
    }
    tableBase = pageTableOffsetWords + virtualPage * pageTableEntryWords;
    uint physicalMetadataOffset = pageTableOffsetWords +
        virtualPageCount * (pageTableEntryWords + pageHistoryWords);
    metadataBase = physicalMetadataOffset +
        physicalPage * physicalMetadataWords;
    uint resourceGeneration = ReadStorageWordUniform(
        pc.ParamsBufferIndex,
        55u);
    if (ReadStorageWordUniform(residencyArena, tableBase + 0u) !=
            physicalPage + 1u ||
        ReadStorageWordUniform(residencyArena, tableBase + 1u) !=
            pageMappingGeneration ||
        ReadStorageWordUniform(residencyArena, metadataBase + 0u) !=
            virtualPage + 1u ||
        ReadStorageWordUniform(residencyArena, metadataBase + 1u) !=
            pageMappingGeneration ||
        ReadStorageWordUniform(residencyArena, metadataBase + 2u) !=
            resourceGeneration)
    {
        return false;
    }

    pageLocalBit = 1u << pageLocal;
    uvec3 pageOrigin = uvec3(pageX, pageY, pageZ) * 2u;
    for (uint slot = 0u; slot < 8u; slot++)
    {
        uvec3 localCoord = uvec3(
            slot & 1u,
            (slot >> 1u) & 1u,
            (slot >> 2u) & 1u);
        uvec3 coord = pageOrigin + localCoord;
        if (coord.x < countX && coord.y < countY && coord.z < countZ)
            validProbeMask |= 1u << slot;
    }
    return validProbeMask != 0u;
}

bool SchedulerSparseVisiblePagePublicationPending(
    uint probeIndex,
    uint volumeIndex,
    uint physicalProbeIndex,
    uint pageMappingGeneration)
{
    const uint tablePublished = 1u << 2u;
    const uint allocationDemandClassShift = 8u;
    const uint receiverMissDemandClass = 5u;
    uint arena;
    uint tableBase;
    uint metadataBase;
    uint pageLocalBit;
    uint validMask;
    if (!SchedulerResolveSparsePageMetadata(
            probeIndex,
            volumeIndex,
            physicalProbeIndex,
            pageMappingGeneration,
            arena,
            tableBase,
            metadataBase,
            pageLocalBit,
            validMask))
    {
        return false;
    }

    uint tableFlags = ReadStorageWordUniform(arena, tableBase + 2u);
    uint allocation = ReadStorageWordUniform(arena, metadataBase + 11u);
    uint allocationDemandClass =
        (allocation >> allocationDemandClassShift) & 0xffu;
    uint publishedMask =
        (ReadStorageWordUniform(arena, metadataBase + 3u) >> 8u) & 0xffu;
    return (tableFlags & tablePublished) == 0u &&
        allocationDemandClass >= receiverMissDemandClass &&
        ReadStorageWordUniform(arena, metadataBase + 8u) != 0u &&
        (publishedMask & validMask) != validMask;
}

bool SchedulerRetainSparsePage(
    uint probeIndex,
    uint volumeIndex,
    uint physicalProbeIndex,
    uint pageMappingGeneration)
{
    const uint sparseMode = 2u;
    const uint pagingBaseWords = 64u + 16u * 28u;
    uint mode = ReadStorageWordUniform(
        pc.ParamsBufferIndex,
        pagingBaseWords + volumeIndex * 8u + 3u);
    if (mode != sparseMode)
        return true;
    uint arena;
    uint tableBase;
    uint metadataBase;
    uint pageLocalBit;
    uint validMask;
    if (!SchedulerResolveSparsePageMetadata(
            probeIndex,
            volumeIndex,
            physicalProbeIndex,
            pageMappingGeneration,
            arena,
            tableBase,
            metadataBase,
            pageLocalBit,
            validMask))
    {
        return false;
    }
    uint observed = atomicAdd(
        BindlessStorageBuffers[nonuniformEXT(arena)].Words[
            metadataBase + 3u],
        0u);
    for (;;)
    {
        uint count = (observed >> 16u) & 0xffu;
        if (count == 0xffu)
            return false;
        uint replacement = observed + (1u << 16u);
        uint previous = atomicCompSwap(
            BindlessStorageBuffers[nonuniformEXT(arena)].Words[
                metadataBase + 3u],
            observed,
            replacement);
        if (previous == observed)
        {
            atomicCompSwap(
                BindlessStorageBuffers[nonuniformEXT(arena)].Words[
                    metadataBase + 9u],
                0u,
                SchedulerFrameIndex() + 1u);
            return true;
        }
        observed = previous;
    }
}

void SchedulerReleaseSparsePage(
    uint probeIndex,
    uint volumeIndex,
    uint physicalProbeIndex,
    uint pageMappingGeneration)
{
    uint arena;
    uint tableBase;
    uint metadataBase;
    uint pageLocalBit;
    uint validMask;
    if (!SchedulerResolveSparsePageMetadata(
            probeIndex,
            volumeIndex,
            physicalProbeIndex,
            pageMappingGeneration,
            arena,
            tableBase,
            metadataBase,
            pageLocalBit,
            validMask))
    {
        return;
    }
    uint observed = atomicAdd(
        BindlessStorageBuffers[nonuniformEXT(arena)].Words[
            metadataBase + 3u],
        0u);
    for (;;)
    {
        uint count = (observed >> 16u) & 0xffu;
        if (count == 0u)
            return;
        uint replacement = observed - (1u << 16u);
        uint previous = atomicCompSwap(
            BindlessStorageBuffers[nonuniformEXT(arena)].Words[
                metadataBase + 3u],
            observed,
            replacement);
        if (previous == observed)
            return;
        observed = previous;
    }
}

void SchedulerPublishSparsePageProbe(
    uint probeIndex,
    uint volumeIndex,
    uint physicalProbeIndex,
    uint pageMappingGeneration)
{
    uint arena;
    uint tableBase;
    uint metadataBase;
    uint pageLocalBit;
    uint validMask;
    if (!SchedulerResolveSparsePageMetadata(
            probeIndex,
            volumeIndex,
            physicalProbeIndex,
            pageMappingGeneration,
            arena,
            tableBase,
            metadataBase,
            pageLocalBit,
            validMask))
    {
        return;
    }
    uint publishedBit = pageLocalBit << 8u;
    uint previous = atomicOr(
        BindlessStorageBuffers[nonuniformEXT(arena)].Words[
            metadataBase + 3u],
        publishedBit);
    uint publishedMask = ((previous | publishedBit) >> 8u) & 0xffu;
    atomicMax(
        BindlessStorageBuffers[nonuniformEXT(arena)].Words[
            metadataBase + 5u],
        SchedulerFrameIndex());
    if ((publishedMask & validMask) == validMask)
    {
        atomicCompSwap(
            BindlessStorageBuffers[nonuniformEXT(arena)].Words[
                metadataBase + 10u],
            0u,
            SchedulerFrameIndex() + 1u);
        atomicOr(
            BindlessStorageBuffers[nonuniformEXT(arena)].Words[
                tableBase + 2u],
            1u << 2u);
    }
}

// Admission owns deterministic selection order but update materialization is
// embarrassingly parallel. Store the five words needed by the materializer in
// the final update slot; the next scheduler stage overwrites the complete
// ten-word proposal before emit can observe it.
void SchedulerWriteUpdateSelection(
    uint updateIndex,
    uint probeIndex,
    uint volumeIndex,
    uint workClassAndTransport,
    uint rayTierAndReason,
    uint expectedGeneration)
{
    uint base = pc.UpdateRecordsOffsetWords +
        updateIndex * SIMPLE_DDGI_SCHEDULER_UPDATE_WORDS;
    SchedulerArenaWrite(base + 0u, probeIndex);
    SchedulerArenaWrite(base + 1u, volumeIndex);
    SchedulerArenaWrite(base + 2u, workClassAndTransport);
    SchedulerArenaWrite(base + 3u, rayTierAndReason);
    SchedulerArenaWrite(base + 4u, expectedGeneration);
}

void SchedulerReadUpdateSelection(
    uint updateIndex,
    out uint probeIndex,
    out uint volumeIndex,
    out uint workClassAndTransport,
    out uint rayTierAndReason,
    out uint expectedGeneration)
{
    uint base = pc.UpdateRecordsOffsetWords +
        updateIndex * SIMPLE_DDGI_SCHEDULER_UPDATE_WORDS;
    probeIndex = SchedulerArenaRead(base + 0u);
    volumeIndex = SchedulerArenaRead(base + 1u);
    workClassAndTransport = SchedulerArenaRead(base + 2u);
    rayTierAndReason = SchedulerArenaRead(base + 3u);
    expectedGeneration = SchedulerArenaRead(base + 4u);
}

void SchedulerWriteUpdate(
    uint updateIndex,
    uint probeIndex,
    uint volumeIndex,
    uint flags,
    uint expectedGeneration,
    uint age,
    uint sourceRayCount,
    uint sourceLightingGeneration,
    uint candidateReasons)
{
    uint base = pc.UpdateRecordsOffsetWords +
        updateIndex * SIMPLE_DDGI_SCHEDULER_UPDATE_WORDS;
    SchedulerArenaWrite(base + 0u, probeIndex);
    SchedulerArenaWrite(base + 1u, volumeIndex);
    SchedulerArenaWrite(base + 2u, flags);
    SchedulerArenaWrite(base + 3u,
        (expectedGeneration & 0x00ffffffu) | (min(age, 255u) << 24u));
    SchedulerArenaWrite(base + 4u, sourceRayCount);
    SchedulerArenaWrite(base + 5u, sourceLightingGeneration);
    // The queue is compacted by ray bucket, so consumers use this stable
    // accepted-order index to address the corresponding outcome record.
    SchedulerArenaWrite(base + 6u, updateIndex);
    uint physicalProbeIndex;
    uint pageMappingGeneration;
    bool addressValid = SchedulerResolvePayloadAddress(
        probeIndex,
        volumeIndex,
        physicalProbeIndex,
        pageMappingGeneration);
    if (addressValid)
    {
        addressValid = SchedulerRetainSparsePage(
            probeIndex,
            volumeIndex,
            physicalProbeIndex,
            pageMappingGeneration);
    }
    SchedulerArenaWrite(
        base + 7u,
        addressValid ? physicalProbeIndex : 0xffffffffu);
    SchedulerArenaWrite(
        base + 8u,
        addressValid ? pageMappingGeneration : 0u);
    uint cacheProbeBaseWordPlusOne = 0u;
    if (addressValid)
    {
        addressValid = TryResolveSimpleDdgiTransportCacheProbeBase(
            SchedulerVolumeCacheBaseWord(volumeIndex),
            SchedulerVolumeCacheWordsPerProbe(volumeIndex),
            SchedulerVolumeCachePhysicalFirstProbe(volumeIndex),
            SchedulerVolumeCachePhysicalProbeCount(volumeIndex),
            physicalProbeIndex,
            cacheProbeBaseWordPlusOne);
    }
    SchedulerArenaWrite(
        base + 9u,
        addressValid ? cacheProbeBaseWordPlusOne : 0u);
    // Candidate reasons are a transaction-private proposal. CommitLocal uses
    // these high bits to apply visibility and invalidation metadata only after
    // every producer has completed successfully. Emit strips them before the
    // record reaches trace, transport, blend, or publication.
    uint privateReasons = 0u;
    if ((candidateReasons & SIMPLE_DDGI_SCHEDULER_REASON_VISIBLE) != 0u)
        privateReasons |= SIMPLE_DDGI_SCHEDULER_PRIVATE_REASON_VISIBLE;
    if ((candidateReasons & SIMPLE_DDGI_SCHEDULER_REASON_REGIONAL_DIRTY) != 0u)
        privateReasons |= SIMPLE_DDGI_SCHEDULER_PRIVATE_REASON_REGIONAL;
    if ((candidateReasons & SIMPLE_DDGI_SCHEDULER_REASON_GLOBAL_DIRTY) != 0u)
        privateReasons |= SIMPLE_DDGI_SCHEDULER_PRIVATE_REASON_GLOBAL;
    if ((candidateReasons & SIMPLE_DDGI_SCHEDULER_REASON_SOURCE_INVALID) != 0u)
        privateReasons |= SIMPLE_DDGI_SCHEDULER_PRIVATE_REASON_SOURCE;
    if ((candidateReasons & SIMPLE_DDGI_SCHEDULER_REASON_TOPOLOGY) != 0u)
        privateReasons |= SIMPLE_DDGI_SCHEDULER_PRIVATE_REASON_TOPOLOGY;
    SchedulerArenaWrite(base + 2u, flags | privateReasons);
}

void SchedulerCopyUpdateToQueue(uint updateIndex, uint queueIndex)
{
    uint sourceBase = pc.UpdateRecordsOffsetWords +
        updateIndex * SIMPLE_DDGI_SCHEDULER_UPDATE_WORDS;
    uint destinationBase = queueIndex * SIMPLE_DDGI_PROBE_UPDATE_STRIDE_WORDS;
    for (uint word = 0u; word < 7u; word++)
    {
        uint value = SchedulerArenaRead(sourceBase + word);
        if (word == 2u)
            value &= ~SIMPLE_DDGI_SCHEDULER_PRIVATE_REASON_MASK;
        WriteStorageWordUniform(
            pc.UpdateQueueBufferIndex,
            destinationBase + word,
            value);
    }
    uint probeIndex = SchedulerArenaRead(sourceBase + 0u);
    uint updateFlags = SchedulerArenaRead(sourceBase + 2u);
    uint stateBase = pc.SchedulerProbeStateOffsetWords +
        probeIndex * SIMPLE_DDGI_SCHEDULER_PROBE_STATE_WORDS;
    uint committedSourceEpoch = SchedulerArenaRead(stateBase + 3u);
    uint transactionSourceEpoch =
        (updateFlags & SIMPLE_DDGI_SCHEDULER_UPDATE_SOURCE_REFRESH) != 0u
            ? (SchedulerUpdatePreservesSourceEpoch(updateFlags)
                ? committedSourceEpoch
                : SchedulerAdvanceSourceEpoch(committedSourceEpoch))
            : committedSourceEpoch;
    // The public queue's final word carries the exact per-probe source epoch
    // reserved by the accepted transaction. Derive it from immutable admitted
    // state here: outcome words 12 and 13 are producer completion counters and
    // must start at zero before trace/transport execute.
    WriteStorageWordUniform(
        pc.UpdateQueueBufferIndex,
        destinationBase + 7u,
        transactionSourceEpoch);
    WriteStorageWordUniform(
        pc.UpdateQueueBufferIndex,
        destinationBase + 8u,
        SchedulerArenaRead(sourceBase + 7u));
    WriteStorageWordUniform(
        pc.UpdateQueueBufferIndex,
        destinationBase + 9u,
        SchedulerArenaRead(sourceBase + 8u));
    WriteStorageWordUniform(
        pc.UpdateQueueBufferIndex,
        destinationBase + 10u,
        ReadStorageWordUniform(pc.ParamsBufferIndex, 55u));
    WriteStorageWordUniform(
        pc.UpdateQueueBufferIndex,
        destinationBase + 11u,
        SchedulerArenaRead(sourceBase + 9u));
}

void SchedulerReadUpdate(
    uint updateIndex,
    out uint probeIndex,
    out uint volumeIndex,
    out uint flags,
    out uint metadata,
    out uint sourceRayCount,
    out uint sourceLightingGeneration)
{
    uint base = pc.UpdateRecordsOffsetWords +
        updateIndex * SIMPLE_DDGI_SCHEDULER_UPDATE_WORDS;
    probeIndex = SchedulerArenaRead(base + 0u);
    volumeIndex = SchedulerArenaRead(base + 1u);
    flags = SchedulerArenaRead(base + 2u);
    metadata = SchedulerArenaRead(base + 3u);
    sourceRayCount = SchedulerArenaRead(base + 4u);
    sourceLightingGeneration = SchedulerArenaRead(base + 5u);
}

uint SchedulerRequiredCompletionMask(uint updateFlags);

void SchedulerWriteOutcome(
    uint outcomeIndex,
    uint probeIndex,
    uint volumeIndex,
    uint expectedGeneration,
    uint sourceRayCount,
    uint rayInvocationCount,
    uint updateFlags)
{
    uint base = pc.OutcomesOffsetWords + outcomeIndex * SIMPLE_DDGI_SCHEDULER_OUTCOME_WORDS;
    SchedulerArenaWrite(base + 0u, SchedulerQueueGeneration());
    SchedulerArenaWrite(base + 1u, SchedulerResourceGeneration());
    SchedulerArenaWrite(base + 2u, SchedulerVolumeGeneration());
    SchedulerArenaWrite(base + 3u, SchedulerSourceGeneration());
    SchedulerArenaWrite(base + 4u, SchedulerTransportGeneration());
    SchedulerArenaWrite(base + 5u, probeIndex);
    SchedulerArenaWrite(base + 6u, expectedGeneration);
    SchedulerArenaWrite(base + 7u, SchedulerRequiredCompletionMask(updateFlags));
    SchedulerArenaWrite(base + 8u, 0u);
    SchedulerArenaWrite(base + 9u, 0u);
    // Word 10 carries the consumer update flags into the commit stage. In
    // particular, a cached-solver update must not erase an already valid
    // source cache or advance its routine-source timestamp.
    SchedulerArenaWrite(base + 10u, updateFlags);
    // The bounded count occupies the low half; blend transactionally appends
    // its quantized quadrature witness in the otherwise unused high half.
    SchedulerArenaWrite(base + 11u, max(rayInvocationCount, 1u) & 0xffffu);
    // Producer completion counters. Trace uses word 12; transport uses word 13.
    // These cannot carry transaction metadata because every ray atomically
    // increments them before CommitLocal validates the outcome.
    SchedulerArenaWrite(base + 12u, 0u);
    SchedulerArenaWrite(base + 13u, 0u);
    SchedulerArenaWrite(base + 14u, 0u);
}

uint SchedulerRequiredCompletionMask(uint updateFlags)
{
    uint required = SIMPLE_DDGI_SCHEDULER_COMPLETE_TRACE |
        SIMPLE_DDGI_SCHEDULER_COMPLETE_RELOCATE |
        SIMPLE_DDGI_SCHEDULER_COMPLETE_BLEND |
        SIMPLE_DDGI_SCHEDULER_COMPLETE_CANONICAL_PUBLISH;
    required |= SchedulerTransportV2()
        ? SIMPLE_DDGI_SCHEDULER_COMPLETE_TRANSPORT
        : SIMPLE_DDGI_SCHEDULER_TRANSPORT_NOT_REQUIRED;
    // Urgent publication mirrors sampled images only after CommitLocal has
    // atomically copied the complete private probe payload to canonical
    // storage. It is therefore an optional post-commit mirror, not a producer
    // prerequisite for this transaction.
    required |= SchedulerSampledPublicationRequired() &&
            !SchedulerUrgentRelight()
        ? SIMPLE_DDGI_SCHEDULER_COMPLETE_SAMPLED_PUBLISH
        : SIMPLE_DDGI_SCHEDULER_SAMPLED_PUBLISH_NOT_REQUIRED;
    return required;
}

void SchedulerMarkOutcomeFailure(uint outcomeIndex, uint reason)
{
    uint base = pc.OutcomesOffsetWords + outcomeIndex * SIMPLE_DDGI_SCHEDULER_OUTCOME_WORDS;
    atomicOr(BindlessStorageBuffers[nonuniformEXT(pc.ArenaBufferIndex)].Words[base + 9u], reason);
}

void SchedulerMarkOutcomeComplete(uint outcomeIndex, uint completionBit)
{
    uint base = pc.OutcomesOffsetWords + outcomeIndex * SIMPLE_DDGI_SCHEDULER_OUTCOME_WORDS;
    atomicOr(BindlessStorageBuffers[nonuniformEXT(pc.ArenaBufferIndex)].Words[base + 8u], completionBit);
}

#endif
