#ifndef NJULF_GI_CAUSTIC_SHARED_GLSL
#define NJULF_GI_CAUSTIC_SHARED_GLSL

// C4 is an isolated tagged-photon workload.  Do not include DDGI shared
// headers here: C4 must neither consume DDGI source radiance nor publish to a
// DDGI atlas/source cache.
#include "common.glsl"

const uint GI_CAUSTIC_ABI_VERSION = 0xC4010004u;
const uint GI_CAUSTIC_TASK_HEADER_WORDS = 16u;
const uint GI_CAUSTIC_TASK_WORDS = 32u;
const uint GI_CAUSTIC_EMITTER_WORDS = 32u;
const uint GI_CAUSTIC_HERO_WORDS = 32u;
const uint GI_CAUSTIC_PROPOSAL_PAIR_WORDS = 8u;
const uint GI_CAUSTIC_PHOTON_WORDS = 20u;
const uint GI_CAUSTIC_CELL_ENTRY_WORDS = 8u;
const uint GI_CAUSTIC_CACHE_HEADER_WORDS = 32u;
const uint GI_CAUSTIC_RESOLVE_REQUEST_WORDS = 16u;
const uint GI_CAUSTIC_RESOLVE_RESULT_WORDS = 12u;
const uint GI_CAUSTIC_REQUIRED_BANK_COUNT = 2u;
const float GI_CAUSTIC_PI = 3.14159265358979323846;

const uint GI_CAUSTIC_TASK_AUTHORED_HERO = 1u << 0u;
const uint GI_CAUSTIC_TASK_MIRROR_HERO = 1u << 1u;
const uint GI_CAUSTIC_TASK_CLOSED_DIELECTRIC_HERO = 1u << 2u;
const uint GI_CAUSTIC_TASK_ROUGH_SPECULAR_REFERENCE = 1u << 3u;
const uint GI_CAUSTIC_TASK_VALIDATED = 1u << 4u;
const uint GI_CAUSTIC_TASK_INVALID = 1u << 31u;

const uint GI_CAUSTIC_PHOTON_SPECULAR_TO_DIFFUSE = 1u << 0u;
const uint GI_CAUSTIC_PHOTON_REFRACTIVE_TO_DIFFUSE = 1u << 1u;
const uint GI_CAUSTIC_PHOTON_FIRST_DIFFUSE_ENDPOINT = 1u << 2u;
const uint GI_CAUSTIC_PHOTON_VALID = 1u << 3u;
const uint GI_CAUSTIC_PHOTON_INVALID = 1u << 31u;

const uint GI_CAUSTIC_CELL_OCCUPIED = 1u << 0u;
const uint GI_CAUSTIC_CELL_BUILD_COMPLETE = 1u << 1u;
const uint GI_CAUSTIC_CELL_INVALID = 1u << 31u;

const uint GI_CAUSTIC_CACHE_INITIALIZED = 1u << 0u;
const uint GI_CAUSTIC_CACHE_BUILD_COMPLETE = 1u << 1u;
const uint GI_CAUSTIC_CACHE_INVALIDATED = 1u << 2u;
const uint GI_CAUSTIC_CACHE_CANDIDATE_OVERFLOW = 1u << 3u;
const uint GI_CAUSTIC_CACHE_CELL_TABLE_OVERFLOW = 1u << 4u;
const uint GI_CAUSTIC_CACHE_TASK_OVERFLOW = 1u << 5u;
const uint GI_CAUSTIC_CACHE_DETERMINISTIC_BACKEND_UNAVAILABLE = 1u << 6u;
const uint GI_CAUSTIC_CACHE_INVALID = 1u << 31u;
const uint GI_CAUSTIC_CACHE_FAILURE_MASK =
    GI_CAUSTIC_CACHE_INVALIDATED |
    GI_CAUSTIC_CACHE_CANDIDATE_OVERFLOW |
    GI_CAUSTIC_CACHE_CELL_TABLE_OVERFLOW |
    GI_CAUSTIC_CACHE_TASK_OVERFLOW |
    GI_CAUSTIC_CACHE_DETERMINISTIC_BACKEND_UNAVAILABLE |
    GI_CAUSTIC_CACHE_INVALID;

const uint GI_CAUSTIC_RESULT_CACHE_READABLE = 1u << 0u;
const uint GI_CAUSTIC_RESULT_CELL_FOUND = 1u << 1u;
const uint GI_CAUSTIC_RESULT_CONTRIBUTION_VALID = 1u << 2u;
const uint GI_CAUSTIC_RESULT_NORMAL_REJECTED = 1u << 3u;
const uint GI_CAUSTIC_RESULT_REVISION_REJECTED = 1u << 4u;
const uint GI_CAUSTIC_RESULT_REQUEST_REJECTED = 1u << 5u;
const uint GI_CAUSTIC_RESULT_CACHE_REJECTED = 1u << 31u;

const uint GI_CAUSTIC_RESOLVE_REQUEST_VALID = 1u << 0u;
const uint GI_CAUSTIC_RESOLVE_REQUEST_OPAQUE = 1u << 1u;
const uint GI_CAUSTIC_RESOLVE_REQUEST_ENERGY_CONSERVING_BRDF = 1u << 2u;
const uint GI_CAUSTIC_RESOLVE_REQUEST_REQUIRED =
    GI_CAUSTIC_RESOLVE_REQUEST_VALID |
    GI_CAUSTIC_RESOLVE_REQUEST_OPAQUE |
    GI_CAUSTIC_RESOLVE_REQUEST_ENERGY_CONSERVING_BRDF;
const uint GI_CAUSTIC_RESOLVE_REQUEST_INVALID = 1u << 31u;

// Scratch header uses a small fixed region.  Remaining scratch is owned by
// the selected deterministic sort/compaction backend.
const uint GI_CAUSTIC_SCRATCH_CANDIDATE_COUNT = 0u;
const uint GI_CAUSTIC_SCRATCH_TASK_REJECTED_COUNT = 1u;
const uint GI_CAUSTIC_SCRATCH_CANDIDATE_OVERFLOW_COUNT = 2u;
const uint GI_CAUSTIC_SCRATCH_CELL_OVERFLOW_COUNT = 3u;
const uint GI_CAUSTIC_SCRATCH_BUILD_REJECTED_COUNT = 4u;
const uint GI_CAUSTIC_SCRATCH_RETAINED_COUNT = 5u;
const uint GI_CAUSTIC_SCRATCH_OCCUPIED_CELL_COUNT = 6u;
const uint GI_CAUSTIC_SCRATCH_METADATA_REJECTED_COUNT = 7u;
const uint GI_CAUSTIC_SCRATCH_HEADER_WORDS = 16u;

const uint GI_CAUSTIC_BUILD_PHASE_CLEAR = 0u;
const uint GI_CAUSTIC_BUILD_PHASE_INITIALIZE_INDICES = 1u;
const uint GI_CAUSTIC_BUILD_PHASE_RADIX_HISTOGRAM = 2u;
const uint GI_CAUSTIC_BUILD_PHASE_RADIX_PREFIX = 3u;
const uint GI_CAUSTIC_BUILD_PHASE_RADIX_SCATTER = 4u;
const uint GI_CAUSTIC_BUILD_PHASE_COMPACT_LOCAL_SCAN = 5u;
const uint GI_CAUSTIC_BUILD_PHASE_COMPACT_GROUP_PREFIX = 6u;
const uint GI_CAUSTIC_BUILD_PHASE_COMPACT_SCATTER = 7u;
const uint GI_CAUSTIC_BUILD_PHASE_STAGE_SORTED_CELLS = 8u;
const uint GI_CAUSTIC_BUILD_PHASE_CLEAR_CELL_TABLE_FOR_HASH = 9u;
const uint GI_CAUSTIC_BUILD_PHASE_HASH_AND_FINALIZE = 10u;
const uint GI_CAUSTIC_BUILD_OPERATION_MASK = 0xffu;
const uint GI_CAUSTIC_RADIX_KEY_SHIFT = 8u;
const uint GI_CAUSTIC_RADIX_BYTE_SHIFT = 16u;
const uint GI_CAUSTIC_RADIX_KEY_COUNT = 7u;
const uint GI_CAUSTIC_RADIX_BYTES_PER_KEY = 4u;
const uint GI_CAUSTIC_RADIX_PASS_COUNT =
    GI_CAUSTIC_RADIX_KEY_COUNT * GI_CAUSTIC_RADIX_BYTES_PER_KEY;
const uint GI_CAUSTIC_RADIX_BIN_COUNT = 256u;
const uint GI_CAUSTIC_BUILD_WORKGROUP_SIZE = 128u;

layout(push_constant) uniform GiCausticPushConstants
{
    uint AbiVersion;
    uint TaskBufferIndex;
    uint PhotonBufferIndex;
    uint CacheBufferIndex;
    uint ScratchBufferIndex;
    uint TaskCount;
    uint PhotonCapacity;
    uint PhotonRecordStrideWords;
    uint CellTableCapacity;
    uint MaximumPhotonsPerCell;
    uint CacheGeneration;
    uint RevisionFingerprintLow;
    uint RevisionFingerprintHigh;
    uint CandidateStagingWordOffset;
    uint CachePhotonBankBaseWord;
    uint PhotonReadBankIndex;
    uint PhotonWriteBankIndex;
    uint CacheReadBankIndex;
    uint CacheWriteBankIndex;
    uint CacheBankHeaderWordOffset;
    uint CacheBankTableWordOffset;
    uint ScratchWordCapacity;
    uint Flags;
    uint BuildPhase;
    uint ResolveRequestWordOffset;
    uint ResolveRequestCount;
    uint TransportAbiVersion;
    uint MaximumOccupiedCells;
    vec4 CellOriginAndSize;
} giCausticPc;

bool GiCausticFinite(float value)
{
    return !isnan(value) && !isinf(value);
}

bool GiCausticFinite(vec3 value)
{
    return GiCausticFinite(value.x) && GiCausticFinite(value.y) &&
        GiCausticFinite(value.z);
}

bool GiCausticFinite(vec4 value)
{
    return GiCausticFinite(value.xyz) && GiCausticFinite(value.w);
}

uint GiCausticBuildWorkgroupCount()
{
    return 1u + (giCausticPc.PhotonCapacity - 1u) /
        GI_CAUSTIC_BUILD_WORKGROUP_SIZE;
}

uint GiCausticIndexBank0WordOffset()
{
    return GI_CAUSTIC_SCRATCH_HEADER_WORDS;
}

uint GiCausticIndexBank1WordOffset()
{
    return GiCausticIndexBank0WordOffset() + giCausticPc.PhotonCapacity;
}

uint GiCausticHistogramWordOffset()
{
    return GiCausticIndexBank1WordOffset() + giCausticPc.PhotonCapacity;
}

uint GiCausticGroupPrefixWordOffset()
{
    return GiCausticHistogramWordOffset() +
        GiCausticBuildWorkgroupCount() * GI_CAUSTIC_RADIX_BIN_COUNT;
}

uint GiCausticBinBaseWordOffset()
{
    return GiCausticGroupPrefixWordOffset() +
        GiCausticBuildWorkgroupCount() * GI_CAUSTIC_RADIX_BIN_COUNT;
}

uint GiCausticRequiredScratchWordCount()
{
    return GiCausticBinBaseWordOffset() + GI_CAUSTIC_RADIX_BIN_COUNT;
}

bool GiCausticDeterministicScratchValid()
{
    uint groups = GiCausticBuildWorkgroupCount();
    uint bank1 = GiCausticIndexBank1WordOffset();
    uint histogram = GiCausticHistogramWordOffset();
    uint prefix = GiCausticGroupPrefixWordOffset();
    uint binBase = GiCausticBinBaseWordOffset();
    uint required = GiCausticRequiredScratchWordCount();
    return groups > 0u && bank1 >= GI_CAUSTIC_SCRATCH_HEADER_WORDS &&
        histogram >= bank1 && prefix >= histogram && binBase >= prefix &&
        required >= binBase && required <= giCausticPc.ScratchWordCapacity;
}

uint GiCausticBuildOperation()
{
    return giCausticPc.BuildPhase & GI_CAUSTIC_BUILD_OPERATION_MASK;
}

uint GiCausticBuildRadixKeyIndex()
{
    return (giCausticPc.BuildPhase >> GI_CAUSTIC_RADIX_KEY_SHIFT) & 0xffu;
}

uint GiCausticBuildRadixByteIndex()
{
    return (giCausticPc.BuildPhase >> GI_CAUSTIC_RADIX_BYTE_SHIFT) & 0x3u;
}

bool GiCausticPushConstantsValid()
{
    return giCausticPc.AbiVersion == GI_CAUSTIC_ABI_VERSION &&
        giCausticPc.TaskCount <= giCausticPc.PhotonCapacity &&
        giCausticPc.PhotonCapacity > 0u &&
        giCausticPc.PhotonRecordStrideWords == GI_CAUSTIC_PHOTON_WORDS &&
        giCausticPc.CellTableCapacity > 0u &&
        (giCausticPc.CellTableCapacity & (giCausticPc.CellTableCapacity - 1u)) == 0u &&
        giCausticPc.MaximumPhotonsPerCell > 0u &&
        giCausticPc.MaximumOccupiedCells > 0u &&
        giCausticPc.MaximumOccupiedCells <= giCausticPc.CellTableCapacity &&
        giCausticPc.CacheGeneration != 0u &&
        giCausticPc.PhotonReadBankIndex < GI_CAUSTIC_REQUIRED_BANK_COUNT &&
        giCausticPc.PhotonWriteBankIndex < GI_CAUSTIC_REQUIRED_BANK_COUNT &&
        giCausticPc.CacheReadBankIndex < GI_CAUSTIC_REQUIRED_BANK_COUNT &&
        giCausticPc.CacheWriteBankIndex < GI_CAUSTIC_REQUIRED_BANK_COUNT &&
        GiCausticFinite(giCausticPc.CellOriginAndSize.xyz) &&
        GiCausticFinite(giCausticPc.CellOriginAndSize.w) &&
        giCausticPc.CellOriginAndSize.w > 0.0 &&
        GiCausticDeterministicScratchValid();
}

uint GiCausticTaskBaseWord(uint taskIndex)
{
    return GI_CAUSTIC_TASK_HEADER_WORDS + taskIndex * GI_CAUSTIC_TASK_WORDS;
}

uint GiCausticCandidateBaseWord(uint candidateIndex)
{
    return giCausticPc.CandidateStagingWordOffset +
        candidateIndex * giCausticPc.PhotonRecordStrideWords;
}

uint GiCausticCachedPhotonBaseWord(uint bankIndex, uint photonIndex)
{
    return giCausticPc.CachePhotonBankBaseWord +
        bankIndex * giCausticPc.PhotonCapacity *
            giCausticPc.PhotonRecordStrideWords +
        photonIndex * giCausticPc.PhotonRecordStrideWords;
}

uint GiCausticScratchRead(uint wordOffset)
{
    return ReadStorageWordUniform(giCausticPc.ScratchBufferIndex, wordOffset);
}

void GiCausticScratchWrite(uint wordOffset, uint value)
{
    WriteStorageWordUniform(giCausticPc.ScratchBufferIndex, wordOffset, value);
}

void GiCausticWriteTaskHeader()
{
    uint index = giCausticPc.TaskBufferIndex;
    WriteStorageWordUniform(index, 0u, GI_CAUSTIC_ABI_VERSION);
    WriteStorageWordUniform(index, 1u, giCausticPc.CacheGeneration);
    WriteStorageWordUniform(index, 2u, giCausticPc.TaskCount);
    WriteStorageWordUniform(index, 3u, giCausticPc.PhotonCapacity);
    WriteStorageWordUniform(index, 4u, giCausticPc.PhotonWriteBankIndex);
    WriteStorageWordUniform(index, 5u, giCausticPc.CacheWriteBankIndex);
    WriteStorageWordUniform(index, 6u, giCausticPc.RevisionFingerprintLow);
    WriteStorageWordUniform(index, 7u, giCausticPc.RevisionFingerprintHigh);
    WriteStorageWordUniform(index, 8u, giCausticPc.Flags);
    for (uint word = 9u; word < GI_CAUSTIC_TASK_HEADER_WORDS; ++word)
        WriteStorageWordUniform(index, word, 0u);
}

bool GiCausticTaskInputValid(uint taskIndex)
{
    if (taskIndex >= giCausticPc.TaskCount)
        return false;
    uint base = GiCausticTaskBaseWord(taskIndex);
    uint flags = ReadStorageWordUniform(giCausticPc.TaskBufferIndex, base + 7u);
    uint heroFlags = flags & (GI_CAUSTIC_TASK_MIRROR_HERO |
        GI_CAUSTIC_TASK_CLOSED_DIELECTRIC_HERO |
        GI_CAUSTIC_TASK_ROUGH_SPECULAR_REFERENCE);
    vec3 origin = vec3(
        ReadStorageFloatUniform(giCausticPc.TaskBufferIndex, base + 8u),
        ReadStorageFloatUniform(giCausticPc.TaskBufferIndex, base + 9u),
        ReadStorageFloatUniform(giCausticPc.TaskBufferIndex, base + 10u));
    vec3 direction = vec3(
        ReadStorageFloatUniform(giCausticPc.TaskBufferIndex, base + 12u),
        ReadStorageFloatUniform(giCausticPc.TaskBufferIndex, base + 13u),
        ReadStorageFloatUniform(giCausticPc.TaskBufferIndex, base + 14u));
    float selectionPdf = ReadStorageFloatUniform(
        giCausticPc.TaskBufferIndex, base + 11u);
    float pathPdf = ReadStorageFloatUniform(
        giCausticPc.TaskBufferIndex, base + 15u);
    vec3 emittedContribution = vec3(
        ReadStorageFloatUniform(giCausticPc.TaskBufferIndex, base + 16u),
        ReadStorageFloatUniform(giCausticPc.TaskBufferIndex, base + 17u),
        ReadStorageFloatUniform(giCausticPc.TaskBufferIndex, base + 18u));
    float positionPdf = ReadStorageFloatUniform(
        giCausticPc.TaskBufferIndex, base + 19u);
    vec3 auditedInitialFlux = vec3(
        ReadStorageFloatUniform(giCausticPc.TaskBufferIndex, base + 20u),
        ReadStorageFloatUniform(giCausticPc.TaskBufferIndex, base + 21u),
        ReadStorageFloatUniform(giCausticPc.TaskBufferIndex, base + 22u));
    float directionPdf = ReadStorageFloatUniform(
        giCausticPc.TaskBufferIndex, base + 23u);
    float ior = ReadStorageFloatUniform(giCausticPc.TaskBufferIndex, base + 24u);
    float roughness = ReadStorageFloatUniform(
        giCausticPc.TaskBufferIndex, base + 25u);
    float initialConeRadius = ReadStorageFloatUniform(
        giCausticPc.TaskBufferIndex, base + 26u);
    float coneSpread = ReadStorageFloatUniform(
        giCausticPc.TaskBufferIndex, base + 27u);
    vec3 absorption = vec3(
        ReadStorageFloatUniform(giCausticPc.TaskBufferIndex, base + 28u),
        ReadStorageFloatUniform(giCausticPc.TaskBufferIndex, base + 29u),
        ReadStorageFloatUniform(giCausticPc.TaskBufferIndex, base + 30u));
    float maximumDistance = ReadStorageFloatUniform(
        giCausticPc.TaskBufferIndex, base + 31u);
    float jointPdf = selectionPdf * pathPdf * positionPdf * directionPdf;
    float denominator = float(giCausticPc.TaskCount) * jointPdf;
    vec3 expectedInitialFlux = emittedContribution / max(denominator, 1.0e-30);
    vec3 fluxTolerance = max(vec3(1.0e-6), abs(expectedInitialFlux) * 2.0e-4);
    bool initialFluxMatches = all(lessThanEqual(
        abs(auditedInitialFlux - expectedInitialFlux), fluxTolerance));
    bool opticsValid = GiCausticFinite(ior) && ior > 0.0 && ior <= 4.0 &&
        GiCausticFinite(roughness) && roughness >= 0.0 && roughness <= 1.0 &&
        GiCausticFinite(initialConeRadius) && initialConeRadius > 0.0 &&
        GiCausticFinite(coneSpread) && coneSpread >= 0.0 &&
        GiCausticFinite(absorption) && all(greaterThanEqual(absorption, vec3(0.0))) &&
        GiCausticFinite(maximumDistance) && maximumDistance > 0.0;
    bool heroModeValid =
        ((heroFlags & GI_CAUSTIC_TASK_MIRROR_HERO) != 0u &&
            roughness <= 0.04) ||
        ((heroFlags & GI_CAUSTIC_TASK_CLOSED_DIELECTRIC_HERO) != 0u &&
            roughness <= 0.04 && ior > 1.0) ||
        ((heroFlags & GI_CAUSTIC_TASK_ROUGH_SPECULAR_REFERENCE) != 0u &&
            roughness > 0.04);
    return ReadStorageWordUniform(giCausticPc.TaskBufferIndex, base) ==
            GI_CAUSTIC_ABI_VERSION &&
        ReadStorageWordUniform(giCausticPc.TaskBufferIndex, base + 1u) ==
            giCausticPc.CacheGeneration &&
        (ReadStorageWordUniform(giCausticPc.TaskBufferIndex, base + 2u) != 0u ||
         ReadStorageWordUniform(giCausticPc.TaskBufferIndex, base + 3u) != 0u) &&
        ReadStorageWordUniform(giCausticPc.TaskBufferIndex, base + 4u) != 0u &&
        ReadStorageWordUniform(giCausticPc.TaskBufferIndex, base + 5u) != 0u &&
        ReadStorageWordUniform(giCausticPc.TaskBufferIndex, base + 6u) != 0u &&
        (flags & GI_CAUSTIC_TASK_AUTHORED_HERO) != 0u &&
        (flags & GI_CAUSTIC_TASK_INVALID) == 0u &&
        heroFlags != 0u && (heroFlags & (heroFlags - 1u)) == 0u &&
        GiCausticFinite(origin) && GiCausticFinite(direction) &&
        GiCausticFinite(selectionPdf) && selectionPdf > 0.0 &&
        GiCausticFinite(pathPdf) && pathPdf > 0.0 &&
        GiCausticFinite(positionPdf) && positionPdf > 0.0 &&
        GiCausticFinite(directionPdf) && directionPdf > 0.0 &&
        GiCausticFinite(emittedContribution) &&
        all(greaterThanEqual(emittedContribution, vec3(0.0))) &&
        GiCausticFinite(auditedInitialFlux) &&
        all(greaterThanEqual(auditedInitialFlux, vec3(0.0))) &&
        GiCausticFinite(jointPdf) && jointPdf > 0.0 &&
        GiCausticFinite(denominator) && denominator > 0.0 &&
        GiCausticFinite(expectedInitialFlux) && initialFluxMatches &&
        opticsValid && heroModeValid &&
        dot(direction, direction) > 0.999 && dot(direction, direction) < 1.001;
}

void GiCausticSetTaskValidated(uint taskIndex, bool valid)
{
    uint base = GiCausticTaskBaseWord(taskIndex);
    uint flags = ReadStorageWordUniform(giCausticPc.TaskBufferIndex, base + 7u);
    flags = valid
        ? (flags | GI_CAUSTIC_TASK_VALIDATED) & ~GI_CAUSTIC_TASK_INVALID
        : (flags | GI_CAUSTIC_TASK_INVALID) & ~GI_CAUSTIC_TASK_VALIDATED;
    WriteStorageWordUniform(giCausticPc.TaskBufferIndex, base + 7u, flags);
}

bool GiCausticTaskWasValidated(uint taskIndex)
{
    uint flags = ReadStorageWordUniform(
        giCausticPc.TaskBufferIndex, GiCausticTaskBaseWord(taskIndex) + 7u);
    return (flags & (GI_CAUSTIC_TASK_VALIDATED | GI_CAUSTIC_TASK_INVALID)) ==
        GI_CAUSTIC_TASK_VALIDATED;
}

bool GiCausticCandidateValid(uint candidateIndex)
{
    if (candidateIndex >= giCausticPc.PhotonCapacity)
        return false;
    uint base = GiCausticCandidateBaseWord(candidateIndex);
    vec3 position = vec3(
        ReadStorageFloatUniform(giCausticPc.PhotonBufferIndex, base + 0u),
        ReadStorageFloatUniform(giCausticPc.PhotonBufferIndex, base + 1u),
        ReadStorageFloatUniform(giCausticPc.PhotonBufferIndex, base + 2u));
    vec3 flux = vec3(
        ReadStorageFloatUniform(giCausticPc.PhotonBufferIndex, base + 4u),
        ReadStorageFloatUniform(giCausticPc.PhotonBufferIndex, base + 5u),
        ReadStorageFloatUniform(giCausticPc.PhotonBufferIndex, base + 6u));
    float supportRadius = ReadStorageFloatUniform(
        giCausticPc.PhotonBufferIndex, base + 3u);
    uint flags = ReadStorageWordUniform(giCausticPc.PhotonBufferIndex, base + 10u);
    return ReadStorageWordUniform(giCausticPc.PhotonBufferIndex, base + 11u) != 0u &&
        ReadStorageWordUniform(giCausticPc.PhotonBufferIndex, base + 16u) != 0u &&
        ReadStorageWordUniform(giCausticPc.PhotonBufferIndex, base + 17u) != 0u &&
        ReadStorageWordUniform(giCausticPc.PhotonBufferIndex, base + 19u) ==
            giCausticPc.CacheGeneration &&
        (flags & (GI_CAUSTIC_PHOTON_VALID |
                  GI_CAUSTIC_PHOTON_FIRST_DIFFUSE_ENDPOINT)) ==
            (GI_CAUSTIC_PHOTON_VALID |
             GI_CAUSTIC_PHOTON_FIRST_DIFFUSE_ENDPOINT) &&
        (flags & GI_CAUSTIC_PHOTON_INVALID) == 0u &&
        GiCausticFinite(position) && GiCausticFinite(flux) &&
        GiCausticFinite(supportRadius) && supportRadius > 0.0 &&
        supportRadius <= giCausticPc.CellOriginAndSize.w;
}

bool GiCausticCellFromPosition(vec3 position, out ivec4 cell)
{
    cell = ivec4(0);
    vec3 relative = (position - giCausticPc.CellOriginAndSize.xyz) /
        giCausticPc.CellOriginAndSize.w;
    if (!GiCausticFinite(relative) ||
        any(greaterThan(abs(relative), vec3(2147480000.0))))
    {
        return false;
    }
    cell = ivec4(ivec3(floor(relative)), 0);
    return true;
}

ivec4 GiCausticCandidateCell(uint candidateIndex, out bool valid)
{
    uint base = GiCausticCandidateBaseWord(candidateIndex);
    vec3 position = vec3(
        ReadStorageFloatUniform(giCausticPc.PhotonBufferIndex, base + 0u),
        ReadStorageFloatUniform(giCausticPc.PhotonBufferIndex, base + 1u),
        ReadStorageFloatUniform(giCausticPc.PhotonBufferIndex, base + 2u));
    ivec4 cell;
    valid = GiCausticCellFromPosition(position, cell);
    return cell;
}

bool GiCausticSameCell(ivec4 left, ivec4 right)
{
    return all(equal(left, right));
}

uint GiCausticHash(uint value)
{
    value ^= value >> 16u;
    value *= 0x7feb352du;
    value ^= value >> 15u;
    value *= 0x846ca68bu;
    value ^= value >> 16u;
    return value;
}

uint GiCausticCellHash(ivec4 cell)
{
    uint state = 0x9e3779b9u;
    state = GiCausticHash(state ^ uint(cell.x));
    state = GiCausticHash(state ^ uint(cell.y));
    state = GiCausticHash(state ^ uint(cell.z));
    return GiCausticHash(state ^ uint(cell.w));
}

int GiCausticCompareInt(int left, int right)
{
    return left < right ? -1 : left > right ? 1 : 0;
}

int GiCausticCompareCandidate(uint leftIndex, uint rightIndex)
{
    bool leftCellValid;
    bool rightCellValid;
    ivec4 leftCell = GiCausticCandidateCell(leftIndex, leftCellValid);
    ivec4 rightCell = GiCausticCandidateCell(rightIndex, rightCellValid);
    if (!leftCellValid || !rightCellValid)
        return leftCellValid ? -1 : rightCellValid ? 1 : 0;
    int comparison = GiCausticCompareInt(leftCell.w, rightCell.w);
    if (comparison != 0) return comparison;
    comparison = GiCausticCompareInt(leftCell.x, rightCell.x);
    if (comparison != 0) return comparison;
    comparison = GiCausticCompareInt(leftCell.y, rightCell.y);
    if (comparison != 0) return comparison;
    comparison = GiCausticCompareInt(leftCell.z, rightCell.z);
    if (comparison != 0) return comparison;
    uint leftBase = GiCausticCandidateBaseWord(leftIndex);
    uint rightBase = GiCausticCandidateBaseWord(rightIndex);
    uint leftStable = ReadStorageWordUniform(giCausticPc.PhotonBufferIndex,
        leftBase + 11u);
    uint rightStable = ReadStorageWordUniform(giCausticPc.PhotonBufferIndex,
        rightBase + 11u);
    if (leftStable != rightStable)
        return leftStable < rightStable ? -1 : 1;
    uint leftSource = ReadStorageWordUniform(giCausticPc.PhotonBufferIndex,
        leftBase + 16u);
    uint rightSource = ReadStorageWordUniform(giCausticPc.PhotonBufferIndex,
        rightBase + 16u);
    if (leftSource != rightSource)
        return leftSource < rightSource ? -1 : 1;
    uint leftHero = ReadStorageWordUniform(giCausticPc.PhotonBufferIndex,
        leftBase + 17u);
    uint rightHero = ReadStorageWordUniform(giCausticPc.PhotonBufferIndex,
        rightBase + 17u);
    if (leftHero != rightHero)
        return leftHero < rightHero ? -1 : 1;
    return leftIndex < rightIndex ? -1 : leftIndex > rightIndex ? 1 : 0;
}

uint GiCausticRadixKey(uint candidateIndex, uint keyIndex)
{
    uint base = GiCausticCandidateBaseWord(candidateIndex);
    if (keyIndex == 0u)
        return ReadStorageWordUniform(giCausticPc.PhotonBufferIndex, base + 17u);
    if (keyIndex == 1u)
        return ReadStorageWordUniform(giCausticPc.PhotonBufferIndex, base + 16u);
    if (keyIndex == 2u)
        return ReadStorageWordUniform(giCausticPc.PhotonBufferIndex, base + 11u);

    bool valid;
    ivec4 cell = GiCausticCandidateCell(candidateIndex, valid);
    if (!valid)
        return 0xffffffffu;
    // XOR maps signed two's-complement coordinates to monotonic unsigned
    // order without losing any of the full 32-bit cell identity.
    if (keyIndex == 3u)
        return uint(cell.z) ^ 0x80000000u;
    if (keyIndex == 4u)
        return uint(cell.y) ^ 0x80000000u;
    if (keyIndex == 5u)
        return uint(cell.x) ^ 0x80000000u;
    return uint(cell.w) ^ 0x80000000u;
}

uint GiCausticRadixDigit(uint candidateIndex, uint keyIndex, uint byteIndex)
{
    return (GiCausticRadixKey(candidateIndex, keyIndex) >>
        (byteIndex * 8u)) & 0xffu;
}

uint GiCausticSortedCandidateIndex(uint sortedRank)
{
    return GiCausticScratchRead(GiCausticIndexBank0WordOffset() + sortedRank);
}

uint GiCausticFindCellRunStart(uint sortedRank, ivec4 cell)
{
    uint low = 0u;
    uint high = sortedRank;
    while (low < high)
    {
        uint middle = low + (high - low) / 2u;
        bool valid;
        ivec4 candidateCell = GiCausticCandidateCell(
            GiCausticSortedCandidateIndex(middle), valid);
        if (valid && GiCausticSameCell(candidateCell, cell))
            high = middle;
        else
            low = middle + 1u;
    }
    return low;
}

uint GiCausticFindCellRunEnd(uint sortedRank, uint candidateCount, ivec4 cell)
{
    uint low = sortedRank + 1u;
    uint high = candidateCount;
    while (low < high)
    {
        uint middle = low + (high - low) / 2u;
        bool valid;
        ivec4 candidateCell = GiCausticCandidateCell(
            GiCausticSortedCandidateIndex(middle), valid);
        if (valid && GiCausticSameCell(candidateCell, cell))
            low = middle + 1u;
        else
            high = middle;
    }
    return low;
}

void GiCausticWriteLinearCellEntry(uint entryIndex, ivec4 cell,
    uint photonOffset, uint photonCount)
{
    uint base = giCausticPc.CacheBankTableWordOffset +
        entryIndex * GI_CAUSTIC_CELL_ENTRY_WORDS;
    WriteStorageWordUniform(giCausticPc.CacheBufferIndex, base + 0u, uint(cell.x));
    WriteStorageWordUniform(giCausticPc.CacheBufferIndex, base + 1u, uint(cell.y));
    WriteStorageWordUniform(giCausticPc.CacheBufferIndex, base + 2u, uint(cell.z));
    WriteStorageWordUniform(giCausticPc.CacheBufferIndex, base + 3u, uint(cell.w));
    WriteStorageWordUniform(giCausticPc.CacheBufferIndex, base + 4u, photonOffset);
    WriteStorageWordUniform(giCausticPc.CacheBufferIndex, base + 5u, photonCount);
    WriteStorageWordUniform(giCausticPc.CacheBufferIndex, base + 6u,
        giCausticPc.CacheGeneration);
    WriteStorageWordUniform(giCausticPc.CacheBufferIndex, base + 7u,
        GI_CAUSTIC_CELL_OCCUPIED | GI_CAUSTIC_CELL_BUILD_COMPLETE);
}

void GiCausticStageLinearCellEntry(uint entryIndex)
{
    uint source = giCausticPc.CacheBankTableWordOffset +
        entryIndex * GI_CAUSTIC_CELL_ENTRY_WORDS;
    uint destination = GiCausticCandidateBaseWord(0u) +
        entryIndex * GI_CAUSTIC_CELL_ENTRY_WORDS;
    for (uint word = 0u; word < GI_CAUSTIC_CELL_ENTRY_WORDS; ++word)
    {
        WriteStorageWordUniform(giCausticPc.PhotonBufferIndex,
            destination + word,
            ReadStorageWordUniform(giCausticPc.CacheBufferIndex, source + word));
    }
}

ivec4 GiCausticReadStagedCellEntry(uint entryIndex,
    out uint photonOffset, out uint photonCount, out bool valid)
{
    uint base = GiCausticCandidateBaseWord(0u) +
        entryIndex * GI_CAUSTIC_CELL_ENTRY_WORDS;
    ivec4 cell = ivec4(
        int(ReadStorageWordUniform(giCausticPc.PhotonBufferIndex, base + 0u)),
        int(ReadStorageWordUniform(giCausticPc.PhotonBufferIndex, base + 1u)),
        int(ReadStorageWordUniform(giCausticPc.PhotonBufferIndex, base + 2u)),
        int(ReadStorageWordUniform(giCausticPc.PhotonBufferIndex, base + 3u)));
    photonOffset = ReadStorageWordUniform(
        giCausticPc.PhotonBufferIndex, base + 4u);
    photonCount = ReadStorageWordUniform(
        giCausticPc.PhotonBufferIndex, base + 5u);
    uint generation = ReadStorageWordUniform(
        giCausticPc.PhotonBufferIndex, base + 6u);
    uint flags = ReadStorageWordUniform(
        giCausticPc.PhotonBufferIndex, base + 7u);
    valid = generation == giCausticPc.CacheGeneration &&
        (flags & (GI_CAUSTIC_CELL_OCCUPIED | GI_CAUSTIC_CELL_BUILD_COMPLETE |
                  GI_CAUSTIC_CELL_INVALID)) ==
            (GI_CAUSTIC_CELL_OCCUPIED | GI_CAUSTIC_CELL_BUILD_COMPLETE) &&
        photonCount > 0u && photonOffset <= giCausticPc.PhotonCapacity &&
        photonCount <= giCausticPc.PhotonCapacity - photonOffset;
    return cell;
}

void GiCausticClearCellEntry(uint entryIndex)
{
    uint base = giCausticPc.CacheBankTableWordOffset +
        entryIndex * GI_CAUSTIC_CELL_ENTRY_WORDS;
    for (uint word = 0u; word < GI_CAUSTIC_CELL_ENTRY_WORDS; ++word)
        WriteStorageWordUniform(giCausticPc.CacheBufferIndex, base + word, 0u);
}

void GiCausticWriteCacheHeader(uint flags, uint candidateInputCount,
    uint candidateCount, uint retainedCount, uint occupiedCellCount,
    uint overflowCount)
{
    uint base = giCausticPc.CacheBankHeaderWordOffset;
    uint bufferIndex = giCausticPc.CacheBufferIndex;
    WriteStorageWordUniform(bufferIndex, base + 0u, GI_CAUSTIC_ABI_VERSION);
    WriteStorageWordUniform(bufferIndex, base + 1u, giCausticPc.CacheGeneration);
    WriteStorageWordUniform(bufferIndex, base + 2u, giCausticPc.RevisionFingerprintLow);
    WriteStorageWordUniform(bufferIndex, base + 3u, giCausticPc.RevisionFingerprintHigh);
    WriteStorageWordUniform(bufferIndex, base + 4u, giCausticPc.TaskCount);
    WriteStorageWordUniform(bufferIndex, base + 5u, giCausticPc.PhotonCapacity);
    WriteStorageWordUniform(bufferIndex, base + 6u,
        giCausticPc.PhotonRecordStrideWords * 4u);
    WriteStorageWordUniform(bufferIndex, base + 7u, giCausticPc.CellTableCapacity);
    WriteStorageWordUniform(bufferIndex, base + 8u, giCausticPc.MaximumPhotonsPerCell);
    WriteStorageWordUniform(bufferIndex, base + 9u, candidateCount);
    WriteStorageWordUniform(bufferIndex, base + 10u, retainedCount);
    WriteStorageWordUniform(bufferIndex, base + 11u, occupiedCellCount);
    WriteStorageWordUniform(bufferIndex, base + 12u, overflowCount);
    WriteStorageWordUniform(bufferIndex, base + 13u, flags);
    WriteStorageWordUniform(bufferIndex, base + 14u, giCausticPc.CacheGeneration);
    WriteStorageWordUniform(bufferIndex, base + 15u, giCausticPc.CacheWriteBankIndex);
    WriteStorageFloatUniform(bufferIndex, base + 16u, giCausticPc.CellOriginAndSize.x);
    WriteStorageFloatUniform(bufferIndex, base + 17u, giCausticPc.CellOriginAndSize.y);
    WriteStorageFloatUniform(bufferIndex, base + 18u, giCausticPc.CellOriginAndSize.z);
    WriteStorageFloatUniform(bufferIndex, base + 19u, giCausticPc.CellOriginAndSize.w);
    WriteStorageWordUniform(bufferIndex, base + 20u, giCausticPc.PhotonWriteBankIndex);
    WriteStorageWordUniform(bufferIndex, base + 21u, candidateInputCount);
    WriteStorageWordUniform(bufferIndex, base + 22u, giCausticPc.TransportAbiVersion);
    for (uint word = 23u; word < GI_CAUSTIC_CACHE_HEADER_WORDS; ++word)
        WriteStorageWordUniform(bufferIndex, base + word, 0u);
}

bool GiCausticCacheHeaderReadable(uint headerWordOffset)
{
    uint bufferIndex = giCausticPc.CacheBufferIndex;
    uint flags = ReadStorageWordUniform(bufferIndex, headerWordOffset + 13u);
    return ReadStorageWordUniform(bufferIndex, headerWordOffset + 0u) ==
            GI_CAUSTIC_ABI_VERSION &&
        ReadStorageWordUniform(bufferIndex, headerWordOffset + 1u) ==
            giCausticPc.CacheGeneration &&
        ReadStorageWordUniform(bufferIndex, headerWordOffset + 2u) ==
            giCausticPc.RevisionFingerprintLow &&
        ReadStorageWordUniform(bufferIndex, headerWordOffset + 3u) ==
            giCausticPc.RevisionFingerprintHigh &&
        ReadStorageWordUniform(bufferIndex, headerWordOffset + 15u) ==
            giCausticPc.CacheReadBankIndex &&
        ReadStorageWordUniform(bufferIndex, headerWordOffset + 20u) ==
            giCausticPc.PhotonReadBankIndex &&
        ReadStorageWordUniform(bufferIndex, headerWordOffset + 22u) ==
            giCausticPc.TransportAbiVersion &&
        (flags & (GI_CAUSTIC_CACHE_INITIALIZED |
                  GI_CAUSTIC_CACHE_BUILD_COMPLETE)) ==
            (GI_CAUSTIC_CACHE_INITIALIZED |
             GI_CAUSTIC_CACHE_BUILD_COMPLETE) &&
        (flags & GI_CAUSTIC_CACHE_FAILURE_MASK) == 0u;
}

bool GiCausticInsertCell(ivec4 cell, uint photonOffset, uint photonCount)
{
    uint mask = giCausticPc.CellTableCapacity - 1u;
    uint start = GiCausticCellHash(cell) & mask;
    for (uint probe = 0u; probe < giCausticPc.CellTableCapacity; ++probe)
    {
        uint entry = (start + probe) & mask;
        uint base = giCausticPc.CacheBankTableWordOffset +
            entry * GI_CAUSTIC_CELL_ENTRY_WORDS;
        uint flags = ReadStorageWordUniform(giCausticPc.CacheBufferIndex,
            base + 7u);
        if ((flags & GI_CAUSTIC_CELL_OCCUPIED) == 0u)
        {
            WriteStorageWordUniform(giCausticPc.CacheBufferIndex, base + 0u,
                uint(cell.x));
            WriteStorageWordUniform(giCausticPc.CacheBufferIndex, base + 1u,
                uint(cell.y));
            WriteStorageWordUniform(giCausticPc.CacheBufferIndex, base + 2u,
                uint(cell.z));
            WriteStorageWordUniform(giCausticPc.CacheBufferIndex, base + 3u,
                uint(cell.w));
            WriteStorageWordUniform(giCausticPc.CacheBufferIndex, base + 4u,
                photonOffset);
            WriteStorageWordUniform(giCausticPc.CacheBufferIndex, base + 5u,
                photonCount);
            WriteStorageWordUniform(giCausticPc.CacheBufferIndex, base + 6u,
                giCausticPc.CacheGeneration);
            WriteStorageWordUniform(giCausticPc.CacheBufferIndex, base + 7u,
                GI_CAUSTIC_CELL_OCCUPIED | GI_CAUSTIC_CELL_BUILD_COMPLETE);
            return true;
        }
        ivec4 existing = ivec4(
            int(ReadStorageWordUniform(giCausticPc.CacheBufferIndex, base + 0u)),
            int(ReadStorageWordUniform(giCausticPc.CacheBufferIndex, base + 1u)),
            int(ReadStorageWordUniform(giCausticPc.CacheBufferIndex, base + 2u)),
            int(ReadStorageWordUniform(giCausticPc.CacheBufferIndex, base + 3u)));
        if (GiCausticSameCell(existing, cell))
            return false;
    }
    return false;
}

bool GiCausticFindCell(ivec4 cell, uint tableWordOffset,
    out uint photonOffset, out uint photonCount)
{
    photonOffset = 0u;
    photonCount = 0u;
    uint mask = giCausticPc.CellTableCapacity - 1u;
    uint start = GiCausticCellHash(cell) & mask;
    for (uint probe = 0u; probe < giCausticPc.CellTableCapacity; ++probe)
    {
        uint entry = (start + probe) & mask;
        uint base = tableWordOffset + entry * GI_CAUSTIC_CELL_ENTRY_WORDS;
        uint flags = ReadStorageWordUniform(giCausticPc.CacheBufferIndex,
            base + 7u);
        if ((flags & GI_CAUSTIC_CELL_OCCUPIED) == 0u)
            return false;
        if ((flags & (GI_CAUSTIC_CELL_BUILD_COMPLETE |
                      GI_CAUSTIC_CELL_INVALID)) != GI_CAUSTIC_CELL_BUILD_COMPLETE ||
            ReadStorageWordUniform(giCausticPc.CacheBufferIndex, base + 6u) !=
                giCausticPc.CacheGeneration)
        {
            return false;
        }
        ivec4 existing = ivec4(
            int(ReadStorageWordUniform(giCausticPc.CacheBufferIndex, base + 0u)),
            int(ReadStorageWordUniform(giCausticPc.CacheBufferIndex, base + 1u)),
            int(ReadStorageWordUniform(giCausticPc.CacheBufferIndex, base + 2u)),
            int(ReadStorageWordUniform(giCausticPc.CacheBufferIndex, base + 3u)));
        if (GiCausticSameCell(existing, cell))
        {
            photonOffset = ReadStorageWordUniform(giCausticPc.CacheBufferIndex,
                base + 4u);
            photonCount = ReadStorageWordUniform(giCausticPc.CacheBufferIndex,
                base + 5u);
            return photonOffset <= giCausticPc.PhotonCapacity &&
                photonCount <= giCausticPc.PhotonCapacity - photonOffset;
        }
    }
    return false;
}

void GiCausticCopyCandidateToCache(uint sourceCandidateIndex,
    uint destinationPhotonIndex, float fluxScale)
{
    uint source = GiCausticCandidateBaseWord(sourceCandidateIndex);
    uint destination = GiCausticCachedPhotonBaseWord(
        giCausticPc.PhotonWriteBankIndex, destinationPhotonIndex);
    for (uint word = 0u; word < GI_CAUSTIC_PHOTON_WORDS; ++word)
    {
        WriteStorageWordUniform(giCausticPc.PhotonBufferIndex, destination + word,
            ReadStorageWordUniform(giCausticPc.PhotonBufferIndex, source + word));
    }
    for (uint component = 0u; component < 3u; ++component)
    {
        float flux = ReadStorageFloatUniform(giCausticPc.PhotonBufferIndex,
            source + 4u + component);
        WriteStorageFloatUniform(giCausticPc.PhotonBufferIndex,
            destination + 4u + component, flux * fluxScale);
    }
}

vec3 GiCausticDecodeOctahedral(uint packed)
{
    int packedX = int(packed << 16u) >> 16;
    int packedY = int(packed) >> 16;
    vec2 encoded = vec2(float(packedX), float(packedY)) / 32767.0;
    vec3 normal = vec3(encoded, 1.0 - abs(encoded.x) - abs(encoded.y));
    if (normal.z < 0.0)
    {
        normal.xy = (1.0 - abs(normal.yx)) * vec2(
            normal.x >= 0.0 ? 1.0 : -1.0,
            normal.y >= 0.0 ? 1.0 : -1.0);
    }
    float lengthSquared = dot(normal, normal);
    return lengthSquared > 1.0e-12 ? normal * inversesqrt(lengthSquared)
        : vec3(0.0, 1.0, 0.0);
}

float GiCausticFootprintWeight(uint photonBase, vec3 receiverPosition)
{
    vec3 photonPosition = vec3(
        ReadStorageFloatUniform(giCausticPc.PhotonBufferIndex, photonBase + 0u),
        ReadStorageFloatUniform(giCausticPc.PhotonBufferIndex, photonBase + 1u),
        ReadStorageFloatUniform(giCausticPc.PhotonBufferIndex, photonBase + 2u));
    vec3 normal = GiCausticDecodeOctahedral(ReadStorageWordUniform(
        giCausticPc.PhotonBufferIndex, photonBase + 9u));
    vec3 basisReference = abs(normal.z) < 0.9
        ? vec3(0.0, 0.0, 1.0) : vec3(0.0, 1.0, 0.0);
    vec3 tangentU = normalize(cross(basisReference, normal));
    vec3 tangentV = cross(normal, tangentU);
    vec3 delta = receiverPosition - photonPosition;
    float axisU = ReadStorageFloatUniform(giCausticPc.PhotonBufferIndex,
        photonBase + 12u);
    float axisV = ReadStorageFloatUniform(giCausticPc.PhotonBufferIndex,
        photonBase + 13u);
    float cosine = ReadStorageFloatUniform(giCausticPc.PhotonBufferIndex,
        photonBase + 14u);
    float sine = ReadStorageFloatUniform(giCausticPc.PhotonBufferIndex,
        photonBase + 15u);
    if (!GiCausticFinite(axisU) || !GiCausticFinite(axisV) ||
        !GiCausticFinite(cosine) || !GiCausticFinite(sine) ||
        axisU <= 0.0 || axisV <= 0.0)
    {
        return 0.0;
    }
    vec2 local = vec2(dot(delta, tangentU), dot(delta, tangentV));
    vec2 rotated = vec2(
        cosine * local.x + sine * local.y,
        -sine * local.x + cosine * local.y);
    float radiusSquared = (rotated.x * rotated.x) / (axisU * axisU) +
        (rotated.y * rotated.y) / (axisV * axisV);
    if (!GiCausticFinite(radiusSquared) || radiusSquared >= 1.0)
        return 0.0;
    // Normalized 2D Epanechnikov footprint: integral over the ellipse is one.
    return 2.0 * (1.0 - radiusSquared) / (GI_CAUSTIC_PI * axisU * axisV);
}

#endif // NJULF_GI_CAUSTIC_SHARED_GLSL
