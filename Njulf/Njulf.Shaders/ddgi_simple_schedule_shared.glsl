#ifndef NJULF_DDGI_SIMPLE_SCHEDULE_SHARED_GLSL
#define NJULF_DDGI_SIMPLE_SCHEDULE_SHARED_GLSL

#include "common.glsl"

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
// The public candidate record is eight words.  The source-ray count is
// derivable from its volume/transport tuple, so the resident input pool stores
// only the first seven words to keep the maximum arena within the memory plan.
const uint SIMPLE_DDGI_SCHEDULER_CANDIDATE_WORDS = 7u;
const uint SIMPLE_DDGI_SCHEDULER_COMPACT_OUTPUT_WORDS = 1u;
const uint SIMPLE_DDGI_SCHEDULER_GROUP_LANE_VALUES_PER_WORD = 2u;
// Public probe state and private scheduler state are different ABIs. Keep the
// public stride here because several scheduler shaders read the public buffer,
// and use the ten-word private stride for the resident arena mirror.
const uint SIMPLE_DDGI_PROBE_STATE_WORDS = 8u;
const uint SIMPLE_DDGI_SCHEDULER_PROBE_STATE_WORDS = 10u;
// The public queue remains an eight-word ABI; the internal scheduler record
// is seven words and is expanded by SchedulerCopyUpdateToQueue. Classification
// proposal bits use scheduler-private high flag bits until emit strips them.
const uint SIMPLE_DDGI_PROBE_UPDATE_STRIDE_WORDS = 8u;
const uint SIMPLE_DDGI_SCHEDULER_UPDATE_WORDS = 7u;
const uint SIMPLE_DDGI_SCHEDULER_OUTCOME_WORDS = 15u;
// The transaction-private relocation proposal reuses the existing 48-byte
// relocation/classification storage ABI. Commit is a scheduler-only shader,
// so mirror that stride here rather than depending on the producer header.
const uint SIMPLE_DDGI_RELOCATION_CLASSIFICATION_STRIDE_WORDS = 12u;
const uint SIMPLE_DDGI_SCHEDULER_INDIRECT_WORDS = 4u;
const uint SIMPLE_DDGI_SCHEDULER_IRRADIANCE_WORDS_PER_PROBE = 128u;
const uint SIMPLE_DDGI_SCHEDULER_VISIBILITY_WORDS_PER_PROBE = 512u;
// Ray-bucket commands and queue metadata are separate regions. The command
// region is passed directly to vkCmdDispatchIndirect; metadata is never part
// of a VkDispatchIndirectCommand.
const uint SIMPLE_DDGI_SCHEDULER_RAY_BUCKET_METADATA_WORDS = 4u;
const uint SIMPLE_DDGI_SCHEDULER_RAY_BUCKET_COMMAND_WORDS = 4u;
const uint SIMPLE_DDGI_SCHEDULER_COUNTER_WORDS = 64u;

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
const uint SIMPLE_DDGI_SCHEDULER_FEATURE_SAMPLED_PUBLICATION = 1u << 8u;

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

// Scheduler-private visibility/publication bits live in the high half of the
// dirty-reason word. The low reason bits are transient; these two bits survive
// commit so the delayed atmosphere summary can identify the visible cohort
// without a CPU probe scan.
const uint SIMPLE_DDGI_SCHEDULER_PROBE_META_VISIBLE = 1u << 16u;
const uint SIMPLE_DDGI_SCHEDULER_PROBE_META_PUBLISHED = 1u << 17u;
const uint SIMPLE_DDGI_SCHEDULER_PROBE_META_REPAIR = 1u << 30u;
const uint SIMPLE_DDGI_SCHEDULER_PROBE_META_VISIBILITY_MASK =
    SIMPLE_DDGI_SCHEDULER_PROBE_META_VISIBLE |
    SIMPLE_DDGI_SCHEDULER_PROBE_META_PUBLISHED;

const uint SIMPLE_DDGI_SCHEDULER_PROBE_FRESH = 1u << 0u;
const uint SIMPLE_DDGI_SCHEDULER_PROBE_SCROLL = 1u << 1u;
const uint SIMPLE_DDGI_SCHEDULER_PROBE_INACTIVE = 1u << 2u;
const uint SIMPLE_DDGI_PROBE_FLAG_FRESH = 1u << 0u;
const uint SIMPLE_DDGI_PROBE_FLAG_SCROLL_EXPOSED = 1u << 1u;
const uint SIMPLE_DDGI_PROBE_FLAG_INACTIVE = 1u << 2u;
const uint SIMPLE_DDGI_PROBE_FLAG_RELOCATION_PENDING = 1u << 3u;
const uint SIMPLE_DDGI_PROBE_FLAG_SOURCE_CACHE_INVALID = 1u << 4u;
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
const uint SIMPLE_DDGI_SCHEDULER_COUNTER_PRIMARY_USED = 9u;
const uint SIMPLE_DDGI_SCHEDULER_COUNTER_SOURCE_USED = 10u;
const uint SIMPLE_DDGI_SCHEDULER_COUNTER_BUCKET_BASE = 20u;
const uint SIMPLE_DDGI_SCHEDULER_COUNTER_VOLUME_USAGE_BASE = 32u;
const uint SIMPLE_DDGI_SCHEDULER_COUNTER_CLASS_USAGE_BASE = 16u;

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
    uint Reserved0;
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

uint SchedulerFrame(uint word)
{
    return SchedulerArenaRead(pc.FrameOffsetWords + word);
}

uint SchedulerActiveProbeCount() { return SchedulerFrame(0u); }
uint SchedulerActiveVolumeCount() { return SchedulerFrame(1u); }
uint SchedulerCandidateCapacity() { return SchedulerFrame(2u); }
uint SchedulerCandidateGroupCount() { return (SchedulerCandidateCapacity() + 63u) / 64u; }
uint SchedulerRequestCapacity() { return SchedulerFrame(3u); }
uint SchedulerRequestBudget() { return SchedulerFrame(5u); }
uint SchedulerPrimaryRayBudget() { return SchedulerFrame(6u); }
uint SchedulerFrameIndex() { return SchedulerFrame(10u); }
uint SchedulerVolumeGeneration() { return SchedulerFrame(14u); }
uint SchedulerResourceGeneration() { return SchedulerFrame(15u); }
uint SchedulerQueueGeneration() { return SchedulerFrame(16u); }
uint SchedulerSourceGeneration() { return SchedulerFrame(17u); }
uint SchedulerTransportGeneration() { return SchedulerFrame(18u); }
uint SchedulerDirtyRegionCount() { return SchedulerFrame(24u); }
uint SchedulerDirtyReasonFlags() { return SchedulerFrame(26u); }
uint SchedulerSourceTargetRays() { return SchedulerFrame(8u); }
uint SchedulerFeatureFlags() { return SchedulerFrame(27u); }
uint SchedulerInvalidationMarker() { return SchedulerFrame(38u); }
bool SchedulerGpuResident() { return (SchedulerFeatureFlags() & 1u) != 0u; }
bool SchedulerGpuMirror() { return (SchedulerFeatureFlags() & 2u) != 0u; }
bool SchedulerTransportV2() { return (SchedulerFeatureFlags() & 4u) != 0u; }
bool SchedulerToroidal() { return (SchedulerFeatureFlags() & 8u) != 0u; }
bool SchedulerAtlasFresh() { return (SchedulerFeatureFlags() & 16u) != 0u; }
bool SchedulerDirtyOverflow() { return (SchedulerFeatureFlags() & 64u) != 0u; }
bool SchedulerClassificationEnabled() { return (SchedulerFeatureFlags() & 128u) != 0u; }
bool SchedulerSampledPublicationRequired() {
    return (SchedulerFeatureFlags() & SIMPLE_DDGI_SCHEDULER_FEATURE_SAMPLED_PUBLICATION) != 0u;
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
    uint expectedGeneration,
    uint sequenceOrdinal,
    uint workClassAndTransport,
    uint rayTierAndReason,
    uint activeRayCount,
    uint sourceRayCount)
{
    SchedulerArenaWrite(base + 0u, probeIndex);
    SchedulerArenaWrite(base + 1u, volumeIndex);
    SchedulerArenaWrite(base + 2u, expectedGeneration);
    SchedulerArenaWrite(base + 3u, sequenceOrdinal);
    SchedulerArenaWrite(base + 4u, workClassAndTransport);
    SchedulerArenaWrite(base + 5u, rayTierAndReason);
    SchedulerArenaWrite(base + 6u, activeRayCount);
    // sourceRayCount is intentionally not stored; admission derives it from
    // the selected volume's full-ray policy for source categories.
}

void SchedulerReadCandidate(
    uint base,
    out uint probeIndex,
    out uint volumeIndex,
    out uint expectedGeneration,
    out uint sequenceOrdinal,
    out uint workClassAndTransport,
    out uint rayTierAndReason,
    out uint activeRayCount,
    out uint sourceRayCount)
{
    probeIndex = SchedulerCandidateWord(base, 0u);
    volumeIndex = SchedulerCandidateWord(base, 1u);
    expectedGeneration = SchedulerCandidateWord(base, 2u);
    sequenceOrdinal = SchedulerCandidateWord(base, 3u);
    workClassAndTransport = SchedulerCandidateWord(base, 4u);
    rayTierAndReason = SchedulerCandidateWord(base, 5u);
    activeRayCount = SchedulerCandidateWord(base, 6u);
    sourceRayCount = 0u;
}

uint SchedulerVolumeWord(uint volumeIndex, uint word)
{
    return SchedulerArenaRead(pc.VolumePolicyOffsetWords +
        volumeIndex * SIMPLE_DDGI_SCHEDULER_VOLUME_POLICY_WORDS + word);
}

uint SchedulerVolumeFirstProbe(uint volumeIndex) { return SchedulerVolumeWord(volumeIndex, 0u); }
uint SchedulerVolumeProbeCount(uint volumeIndex) { return SchedulerVolumeWord(volumeIndex, 1u); }
uint SchedulerVolumeRing(uint volumeIndex) { return SchedulerVolumeWord(volumeIndex, 3u); }
uint SchedulerVolumeFullRays(uint volumeIndex) { return SchedulerVolumeWord(volumeIndex, 30u); }
uint SchedulerVolumeMaintenanceRays(uint volumeIndex) { return SchedulerVolumeWord(volumeIndex, 31u); }
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
uint SchedulerVolumeDirtyGeneration(uint volumeIndex) { return SchedulerVolumeWord(volumeIndex, 39u); }
float SchedulerVolumeProximityRadiusPadding(uint volumeIndex)
{
    return uintBitsToFloat(SchedulerVolumeWord(volumeIndex, 40u));
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
        uint base = pc.DirtyRegionOffsetWords + i * 12u;
        vec3 minimum = vec3(
            uintBitsToFloat(SchedulerArenaRead(base + 0u)),
            uintBitsToFloat(SchedulerArenaRead(base + 1u)),
            uintBitsToFloat(SchedulerArenaRead(base + 2u))) - vec3(expand);
        vec3 maximum = vec3(
            uintBitsToFloat(SchedulerArenaRead(base + 4u)),
            uintBitsToFloat(SchedulerArenaRead(base + 5u)),
            uintBitsToFloat(SchedulerArenaRead(base + 6u))) + vec3(expand);
        if (all(greaterThanEqual(position, minimum)) && all(lessThanEqual(position, maximum)))
            reasons |= SchedulerArenaRead(base + 8u);
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

void SchedulerWriteGroupLaneValue(uint group, uint lane, uint value)
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
        uint replacement = (observed & ~mask) | ((min(value, 0xffffu) & 0xffffu) << shift);
        uint previous = atomicCompSwap(
            BindlessStorageBuffers[nonuniformEXT(pc.ArenaBufferIndex)].Words[wordOffset],
            observed,
            replacement);
        if (previous == observed)
            return;
        observed = previous;
    }
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
    for (uint word = 0u; word < SIMPLE_DDGI_SCHEDULER_UPDATE_WORDS; word++)
    {
        uint value = SchedulerArenaRead(sourceBase + word);
        if (word == 2u)
            value &= ~SIMPLE_DDGI_SCHEDULER_PRIVATE_REASON_MASK;
        WriteStorageWordUniform(
            pc.UpdateQueueBufferIndex,
            destinationBase + word,
            value);
    }
    // The public queue has one padding word beyond the seven-word internal
    // record. Keep that queue ABI fixed and explicit.
    WriteStorageWordUniform(pc.UpdateQueueBufferIndex, destinationBase + 7u, 0u);
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
    SchedulerArenaWrite(base + 11u, max(rayInvocationCount, 1u));
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
    required |= SchedulerSampledPublicationRequired()
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
