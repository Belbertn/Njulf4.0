#ifndef NJULF_DDGI_GUIDING_TRANSPORT_GLSL
#define NJULF_DDGI_GUIDING_TRANSPORT_GLSL

#include "ddgi_guiding_arithmetic.glsl"

// Compact consumer-side mirror of GPUSimpleDdgiGuidingSamplePayload. The
// standalone train/build/sample shaders include the larger hierarchy ABI;
// ordinary DDGI transport needs only these 16 words and the exact estimator.
const uint SIMPLE_DDGI_GUIDING_TRANSPORT_ABI_VERSION = 0x4333000au;
const uint SIMPLE_DDGI_GUIDING_PAYLOAD_WORDS = 16u;
const uint SIMPLE_DDGI_GUIDING_TECHNIQUE_UNIFORM_MAINTENANCE = 0u;
const uint SIMPLE_DDGI_GUIDING_TECHNIQUE_MIXTURE = 1u;
const uint SIMPLE_DDGI_GUIDING_BRANCH_UNIFORM = 0u;
const uint SIMPLE_DDGI_GUIDING_BRANCH_GUIDED = 1u;
const uint SIMPLE_DDGI_GUIDING_SAMPLE_UNIFORM_MAINTENANCE = 1u << 0u;
const uint SIMPLE_DDGI_GUIDING_SAMPLE_MIXTURE_UNIFORM_BRANCH = 1u << 1u;
const uint SIMPLE_DDGI_GUIDING_SAMPLE_MIXTURE_GUIDED_BRANCH = 1u << 2u;
const uint SIMPLE_DDGI_GUIDING_SAMPLE_UNIFORM_FALLBACK = 1u << 3u;
const uint SIMPLE_DDGI_GUIDING_SAMPLE_INVALID_DISTRIBUTION = 1u << 4u;
const uint SIMPLE_DDGI_GUIDING_SAMPLE_KNOWN_FLAGS =
    SIMPLE_DDGI_GUIDING_SAMPLE_UNIFORM_MAINTENANCE |
    SIMPLE_DDGI_GUIDING_SAMPLE_MIXTURE_UNIFORM_BRANCH |
    SIMPLE_DDGI_GUIDING_SAMPLE_MIXTURE_GUIDED_BRANCH |
    SIMPLE_DDGI_GUIDING_SAMPLE_UNIFORM_FALLBACK |
    SIMPLE_DDGI_GUIDING_SAMPLE_INVALID_DISTRIBUTION;
const float SIMPLE_DDGI_GUIDING_UNIFORM_SPHERE_PDF =
    1.0 / (4.0 * SIMPLE_DDGI_PI);
const uint SIMPLE_DDGI_GUIDING_MINIMUM_MAINTENANCE_RAYS = 8u;

#include "ddgi_guiding_payload_identity.glsl"
#include "ddgi_guiding_trace_stage.glsl"

struct SimpleDdgiGuidingTransportPayload
{
    uvec2 stableProbeId;
    uint physicalProbeIndex;
    uint virtualProbeId;
    uint pageGeneration;
    uint distributionGeneration;
    uint proposalEpoch;
    uint slotIndex;
    uint technique;
    uint branch;
    uint sourceEpoch;
    uint sourceLightingGeneration;
    uint packedDirectionOct32;
    float generationTimeMixturePdf;
    uint flags;
    vec3 direction;
};

uint SimpleDdgiGuidingMaintenanceRayCount(uint totalRayCount)
{
    if (totalRayCount == 0u)
        return 0u;
    uint fractional = (totalRayCount + 3u) / 4u;
    return min(totalRayCount, max(
        SIMPLE_DDGI_GUIDING_MINIMUM_MAINTENANCE_RAYS,
        fractional));
}

bool SimpleDdgiGuidingIsMaintenanceSlot(
    uint slotIndex,
    uint totalRayCount)
{
    if (totalRayCount == 0u || slotIndex >= totalRayCount)
        return false;
    uint maintenanceCount = SimpleDdgiGuidingMaintenanceRayCount(
        totalRayCount);
    // This is the exact inverse of the scheduler's stratified subset mapping:
    // floor(k * total / maintenance). It keeps visibility directions spread
    // over the sphere instead of clustering the fixed subset at low ordinals.
    uint rank = (slotIndex * maintenanceCount + totalRayCount - 1u) /
        totalRayCount;
    return slotIndex == rank * totalRayCount / maintenanceCount;
}

float SimpleDdgiGuidingBalanceDenominator(
    uint maintenanceSampleCount,
    uint mixtureSampleCount,
    float generationTimeMixturePdf)
{
    if ((maintenanceSampleCount | mixtureSampleCount) == 0u ||
        isnan(generationTimeMixturePdf) ||
        isinf(generationTimeMixturePdf) ||
        generationTimeMixturePdf <= 0.0)
    {
        return 0.0;
    }
    return float(maintenanceSampleCount) *
            SIMPLE_DDGI_GUIDING_UNIFORM_SPHERE_PDF +
        float(mixtureSampleCount) * generationTimeMixturePdf;
}

float SimpleDdgiGuidingTrainingPdf(
    SimpleDdgiGuidingTransportPayload payload)
{
    return payload.technique ==
            SIMPLE_DDGI_GUIDING_TECHNIQUE_UNIFORM_MAINTENANCE
        ? SIMPLE_DDGI_GUIDING_UNIFORM_SPHERE_PDF
        : payload.generationTimeMixturePdf;
}

bool SimpleDdgiGuidingPayloadOwnsVisibility(
    SimpleDdgiGuidingTransportPayload payload)
{
    return payload.technique ==
        SIMPLE_DDGI_GUIDING_TECHNIQUE_UNIFORM_MAINTENANCE;
}

// A compact record persists with its physical sparse slot, so queue reordering
// is harmless and cached source sequences can reuse their original proposal.
// A different stable id means that the slot has changed owner and the record is
// simply absent for this probe.  Once the stable id matches, however, every
// other identity component is authenticated by the ownership tag and a
// mismatch is a hard validation failure for the current transaction.
bool SimpleDdgiGuidingTracePayloadOwnerMatches(
    uint rayScratchBufferIndex,
    uint firstPayloadWord,
    uvec2 expectedStableProbeId,
    uint physicalProbeIndex,
    uint virtualProbeId,
    uint pageGeneration,
    uint expectedSourceEpoch,
    uint expectedSourceLightingGeneration,
    uint directionSlot,
    out bool stableIdMatches,
    out uint packedDirectionOct32)
{
    packedDirectionOct32 = ReadStorageWordUniform(
        rayScratchBufferIndex,
        firstPayloadWord + SIMPLE_DDGI_GUIDING_TRACE_DIRECTION_WORD);
    uvec2 storedStableProbeId = uvec2(
        ReadStorageWordUniform(
            rayScratchBufferIndex,
            firstPayloadWord + SIMPLE_DDGI_GUIDING_TRACE_STABLE_LOW_WORD),
        ReadStorageWordUniform(
            rayScratchBufferIndex,
            firstPayloadWord + SIMPLE_DDGI_GUIDING_TRACE_STABLE_HIGH_WORD));
    stableIdMatches = any(notEqual(expectedStableProbeId, uvec2(0u))) &&
        all(equal(storedStableProbeId, expectedStableProbeId));
    if (!stableIdMatches)
        return false;

    uint storedOwnershipTag = ReadStorageWordUniform(
        rayScratchBufferIndex,
        firstPayloadWord + SIMPLE_DDGI_GUIDING_TRACE_OWNERSHIP_TAG_WORD);
    return storedOwnershipTag == SimpleDdgiGuidingTraceOwnershipTag(
        expectedStableProbeId,
        physicalProbeIndex,
        virtualProbeId,
        pageGeneration,
        expectedSourceEpoch,
        expectedSourceLightingGeneration,
        directionSlot,
        packedDirectionOct32);
}

// Reads the compact C3 record published into the reserved tail of the ordinary
// DDGI ray scratch. The C3 sample kernel performs the full persistent-payload
// identity/flag/PDF validation before writing these fields and publishes the
// release marker last. Consumers repeat only the finite and estimator checks
// they use; this keeps the large source-cache validation graph out of the
// native-driver-sensitive DDGI programs.
bool TryReadSimpleDdgiGuidingTracePayload(
    uint rayScratchBufferIndex,
    uint firstStageWord,
    uint physicalProbeIndex,
    uint virtualProbeId,
    uint pageGeneration,
    uint expectedSourceEpoch,
    uint expectedSourceLightingGeneration,
    uvec2 expectedStableProbeId,
    uint directionSlot,
    uint directionSlotsPerProbe,
    uint guidedPhysicalProbeCapacity,
    out bool recordPresent,
    out SimpleDdgiGuidingTransportPayload payload)
{
    recordPresent = false;
    payload.stableProbeId = expectedStableProbeId;
    payload.physicalProbeIndex = physicalProbeIndex;
    payload.virtualProbeId = virtualProbeId;
    payload.pageGeneration = pageGeneration;
    payload.distributionGeneration = 0u;
    payload.proposalEpoch = 0u;
    payload.slotIndex = 0u;
    payload.technique = SIMPLE_DDGI_GUIDING_TECHNIQUE_UNIFORM_MAINTENANCE;
    payload.branch = SIMPLE_DDGI_GUIDING_BRANCH_UNIFORM;
    payload.sourceEpoch = expectedSourceEpoch;
    payload.sourceLightingGeneration = expectedSourceLightingGeneration;
    payload.packedDirectionOct32 = 0u;
    payload.generationTimeMixturePdf = 0.0;
    payload.flags = 0u;
    payload.direction = vec3(0.0, 1.0, 0.0);

    if (guidedPhysicalProbeCapacity == 0u ||
        physicalProbeIndex >= guidedPhysicalProbeCapacity ||
        directionSlotsPerProbe == 0u ||
        directionSlotsPerProbe > SIMPLE_DDGI_MAX_RAYS_PER_PROBE ||
        directionSlot >= directionSlotsPerProbe)
    {
        return false;
    }

    uint globalRayBase;
    if (!SimpleDdgiGuidingTryMultiplyU32(
            physicalProbeIndex,
            directionSlotsPerProbe,
            globalRayBase) ||
        directionSlot > 0xffffffffu - globalRayBase)
    {
        return false;
    }
    uint globalRayIndex = globalRayBase + directionSlot;
    uint firstPayloadWord;
    uint markerWord;
    if (!TryResolveSimpleDdgiGuidingTraceStageWords(
            firstStageWord,
            globalRayIndex,
            firstPayloadWord,
            markerWord))
    {
        return false;
    }

    uint marker = ReadStorageWordUniform(
        rayScratchBufferIndex,
        markerWord);
    recordPresent = marker != 0u;
    if (marker != SIMPLE_DDGI_GUIDING_TRACE_STAGE_VALID)
        return false;

    bool stableIdMatches;
    bool ownerMatches = SimpleDdgiGuidingTracePayloadOwnerMatches(
        rayScratchBufferIndex,
        firstPayloadWord,
        expectedStableProbeId,
        physicalProbeIndex,
        virtualProbeId,
        pageGeneration,
        expectedSourceEpoch,
        expectedSourceLightingGeneration,
        directionSlot,
        stableIdMatches,
        payload.packedDirectionOct32);
    if (!stableIdMatches)
    {
        recordPresent = false;
        return false;
    }
    if (!ownerMatches)
        return false;

    payload.generationTimeMixturePdf = uintBitsToFloat(
        ReadStorageWordUniform(
            rayScratchBufferIndex,
            firstPayloadWord + SIMPLE_DDGI_GUIDING_TRACE_PDF_WORD));
    uint techniqueAndBranch = ReadStorageWordUniform(
        rayScratchBufferIndex,
        firstPayloadWord + SIMPLE_DDGI_GUIDING_TRACE_TECHNIQUE_WORD);
    payload.technique = techniqueAndBranch & 0xffu;
    payload.branch = (techniqueAndBranch >> 8u) & 0xffu;
    payload.flags = ReadStorageWordUniform(
        rayScratchBufferIndex,
        firstPayloadWord + SIMPLE_DDGI_GUIDING_TRACE_FLAGS_WORD);
    payload.slotIndex = directionSlot;
    uint expectedTechnique = SimpleDdgiGuidingIsMaintenanceSlot(
            directionSlot,
            directionSlotsPerProbe)
        ? SIMPLE_DDGI_GUIDING_TECHNIQUE_UNIFORM_MAINTENANCE
        : SIMPLE_DDGI_GUIDING_TECHNIQUE_MIXTURE;
    bool techniqueValid = payload.technique == expectedTechnique;
    bool branchValid = payload.branch == SIMPLE_DDGI_GUIDING_BRANCH_UNIFORM ||
        payload.branch == SIMPLE_DDGI_GUIDING_BRANCH_GUIDED;
    bool maintenanceValid = payload.technique !=
            SIMPLE_DDGI_GUIDING_TECHNIQUE_UNIFORM_MAINTENANCE ||
        payload.branch == SIMPLE_DDGI_GUIDING_BRANCH_UNIFORM;
    uint estimatorFlags = payload.flags &
        ~SIMPLE_DDGI_GUIDING_SAMPLE_UNIFORM_FALLBACK;
    uint expectedEstimatorFlag = payload.technique ==
            SIMPLE_DDGI_GUIDING_TECHNIQUE_UNIFORM_MAINTENANCE
        ? SIMPLE_DDGI_GUIDING_SAMPLE_UNIFORM_MAINTENANCE
        : payload.branch == SIMPLE_DDGI_GUIDING_BRANCH_UNIFORM
            ? SIMPLE_DDGI_GUIDING_SAMPLE_MIXTURE_UNIFORM_BRANCH
            : SIMPLE_DDGI_GUIDING_SAMPLE_MIXTURE_GUIDED_BRANCH;
    bool flagsValid = (payload.flags &
            ~SIMPLE_DDGI_GUIDING_SAMPLE_KNOWN_FLAGS) == 0u &&
        (payload.flags &
            SIMPLE_DDGI_GUIDING_SAMPLE_INVALID_DISTRIBUTION) == 0u &&
        estimatorFlags == expectedEstimatorFlag;
    bool pdfValid = !isnan(payload.generationTimeMixturePdf) &&
        !isinf(payload.generationTimeMixturePdf) &&
        payload.generationTimeMixturePdf >=
            0.10 * SIMPLE_DDGI_GUIDING_UNIFORM_SPHERE_PDF;
    if (!techniqueValid || !branchValid || !maintenanceValid ||
        !flagsValid || !pdfValid)
    {
        return false;
    }

    payload.direction = UnpackSimpleDdgiTransportOctDirection(
        payload.packedDirectionOct32);
    float directionLengthSquared = dot(payload.direction, payload.direction);
    return !any(isnan(payload.direction)) &&
        !any(isinf(payload.direction)) &&
        directionLengthSquared > 0.999 && directionLengthSquared < 1.001;
}

bool TryResolveSimpleDdgiGuidingTransportPayloadRange(
    uint physicalProbeIndex,
    uint directionSlot,
    uint directionSlotCount,
    uint directionSlotsPerProbe,
    uint sidecarPhysicalProbeCapacity,
    out uint firstWord)
{
    firstWord = 0u;
    if (sidecarPhysicalProbeCapacity == 0u ||
        physicalProbeIndex >= sidecarPhysicalProbeCapacity ||
        directionSlotsPerProbe == 0u ||
        directionSlotsPerProbe > SIMPLE_DDGI_MAX_RAYS_PER_PROBE ||
        directionSlotCount == 0u ||
        directionSlot >= directionSlotsPerProbe ||
        directionSlotCount > directionSlotsPerProbe - directionSlot)
    {
        return false;
    }

    uint probePayloadBase;
    if (!SimpleDdgiGuidingTryMultiplyU32(
            physicalProbeIndex,
            directionSlotsPerProbe,
            probePayloadBase) ||
        directionSlot > 0xffffffffu - probePayloadBase)
    {
        return false;
    }
    uint firstPayload = probePayloadBase + directionSlot;
    if (directionSlotCount - 1u > 0xffffffffu - firstPayload)
        return false;
    uint lastPayload = firstPayload + directionSlotCount - 1u;
    uint lastWord;
    if (!SimpleDdgiGuidingTryMultiplyU32(
            firstPayload,
            SIMPLE_DDGI_GUIDING_PAYLOAD_WORDS,
            firstWord) ||
        !SimpleDdgiGuidingTryMultiplyU32(
            lastPayload,
            SIMPLE_DDGI_GUIDING_PAYLOAD_WORDS,
            lastWord))
    {
        return false;
    }
    return true;
}

bool SimpleDdgiGuidingTransportProbeHasCompleteBacking(
    uint physicalProbeIndex,
    uint virtualProbeId,
    uint pageGeneration,
    uint expectedSourceEpoch,
    uint expectedSourceLightingGeneration,
    uvec2 expectedStableProbeId,
    uint directionSlotsPerProbe,
    uint sidecarPhysicalProbeCapacity)
{
    uint firstWord;
    if (!TryResolveSimpleDdgiGuidingTransportPayloadRange(
            physicalProbeIndex,
            0u,
            directionSlotsPerProbe,
            directionSlotsPerProbe,
            sidecarPhysicalProbeCapacity,
            firstWord))
    {
        return false;
    }
    uint sidecarIndex = uint(
        SIMPLE_DDGI_GUIDING_DIRECTION_PDF_SIDECAR_BUFFER_INDEX);

    // Allocation cardinality alone is not activation. A zero-filled new
    // sidecar and a record retained from another sparse-slot owner both take
    // the canonical uniform path. Once the first payload names the current
    // owner, however, every malformed field remains a hard validation failure
    // rather than silently mixing guided and uniform proposal families.
    uint abiVersion = ReadStorageWordUniform(sidecarIndex, firstWord + 0u);
    uvec2 stableProbeId = uvec2(
        ReadStorageWordUniform(sidecarIndex, firstWord + 1u),
        ReadStorageWordUniform(sidecarIndex, firstWord + 2u));
    uint storedPhysicalProbeIndex = ReadStorageWordUniform(
        sidecarIndex, firstWord + 3u);
    uint storedVirtualProbeId = ReadStorageWordUniform(
        sidecarIndex, firstWord + 4u);
    uint storedPageGeneration = ReadStorageWordUniform(
        sidecarIndex, firstWord + 5u);
    uint storedSourceEpoch = ReadStorageWordUniform(
        sidecarIndex, firstWord + 10u);
    uint storedSourceLightingGeneration = ReadStorageWordUniform(
        sidecarIndex, firstWord + 11u);
    return abiVersion != 0u &&
        any(notEqual(expectedStableProbeId, uvec2(0u))) &&
        all(equal(stableProbeId, expectedStableProbeId)) &&
        storedPhysicalProbeIndex == physicalProbeIndex &&
        storedVirtualProbeId == virtualProbeId &&
        storedPageGeneration == pageGeneration &&
        storedSourceEpoch == expectedSourceEpoch &&
        storedSourceLightingGeneration == expectedSourceLightingGeneration;
}

// Trace consumes only the sampled direction. The C3 sample/validate stages own
// the complete technique/PDF contract and publish slot 203 only after that
// contract succeeds. The payload tail is aligned, so this path performs one
// vector load and authenticates its direction against the current sparse-slot
// identity instead of inheriting the full 16-word validation graph.
bool TryReadSimpleDdgiGuidingTraceDirection(
    uint physicalProbeIndex,
    uint virtualProbeId,
    uint pageGeneration,
    uint expectedSourceEpoch,
    uint expectedSourceLightingGeneration,
    uvec2 expectedStableProbeId,
    uint directionSlot,
    uint directionSlotsPerProbe,
    uint sidecarPhysicalProbeCapacity,
    out bool recordPresent,
    out vec3 direction)
{
    recordPresent = false;
    direction = vec3(0.0, 1.0, 0.0);
    uint baseWord;
    if (!TryResolveSimpleDdgiGuidingTransportPayloadRange(
            physicalProbeIndex,
            directionSlot,
            1u,
            directionSlotsPerProbe,
            sidecarPhysicalProbeCapacity,
            baseWord))
    {
        return false;
    }

    uint sidecarIndex = uint(
        SIMPLE_DDGI_GUIDING_DIRECTION_PDF_SIDECAR_BUFFER_INDEX);
    uint abiVersion = ReadStorageWordUniform(sidecarIndex, baseWord + 0u);
    uvec2 stableProbeId = uvec2(
        ReadStorageWordUniform(sidecarIndex, baseWord + 1u),
        ReadStorageWordUniform(sidecarIndex, baseWord + 2u));
    uint storedPhysicalProbeIndex = ReadStorageWordUniform(
        sidecarIndex, baseWord + 3u);
    uint storedVirtualProbeId = ReadStorageWordUniform(
        sidecarIndex, baseWord + 4u);
    uint storedPageGeneration = ReadStorageWordUniform(
        sidecarIndex, baseWord + 5u);
    bool ownershipPresent = abiVersion != 0u &&
        any(notEqual(expectedStableProbeId, uvec2(0u))) &&
        all(equal(stableProbeId, expectedStableProbeId)) &&
        storedPhysicalProbeIndex == physicalProbeIndex &&
        storedVirtualProbeId == virtualProbeId &&
        storedPageGeneration == pageGeneration;
    if (!ownershipPresent)
        return false;

    uvec4 payloadIdentity = ReadStorageAlignedUVec4Uniform(
        sidecarIndex,
        baseWord + 8u);
    // A light/source boundary intentionally retires the old proposal family.
    // Treat that payload as absent so the new source sequence can use its
    // deterministic uniform direction. Only a payload claiming the current
    // source identity is considered present and therefore fail-closed below
    // when its ABI, slot, ownership tag, or direction is malformed.
    recordPresent = payloadIdentity.z == expectedSourceEpoch &&
        payloadIdentity.w == expectedSourceLightingGeneration;
    if (!recordPresent)
        return false;

    uvec4 payloadTail = ReadStorageAlignedUVec4Uniform(
        sidecarIndex,
        baseWord + 12u);
    uint expectedOwnershipTag = SimpleDdgiGuidingTraceOwnershipTag(
        expectedStableProbeId,
        physicalProbeIndex,
        virtualProbeId,
        pageGeneration,
        expectedSourceEpoch,
        expectedSourceLightingGeneration,
        directionSlot,
        payloadTail.x);
    if (abiVersion != SIMPLE_DDGI_GUIDING_TRANSPORT_ABI_VERSION ||
        payloadIdentity.x != directionSlot ||
        payloadIdentity.z != expectedSourceEpoch ||
        payloadIdentity.w != expectedSourceLightingGeneration ||
        payloadTail.w != expectedOwnershipTag)
    {
        return false;
    }

    direction = UnpackSimpleDdgiTransportOctDirection(
        payloadTail.x);
    float directionLengthSquared = dot(direction, direction);
    return !any(isnan(direction)) && !any(isinf(direction)) &&
        directionLengthSquared > 0.999 && directionLengthSquared < 1.001;
}

bool TryReadSimpleDdgiGuidingTransportPayload(
    uint physicalProbeIndex,
    uint virtualProbeId,
    uint pageGeneration,
    uint expectedSourceEpoch,
    uint expectedSourceLightingGeneration,
    uvec2 expectedStableProbeId,
    uint directionSlot,
    uint directionSlotsPerProbe,
    uint sidecarPhysicalProbeCapacity,
    out bool recordPresent,
    out SimpleDdgiGuidingTransportPayload payload)
{
    recordPresent = false;
    payload.stableProbeId = uvec2(0u);
    payload.physicalProbeIndex = 0u;
    payload.virtualProbeId = 0u;
    payload.pageGeneration = 0u;
    payload.distributionGeneration = 0u;
    payload.proposalEpoch = 0u;
    payload.slotIndex = 0u;
    payload.technique = SIMPLE_DDGI_GUIDING_TECHNIQUE_UNIFORM_MAINTENANCE;
    payload.branch = SIMPLE_DDGI_GUIDING_BRANCH_UNIFORM;
    payload.sourceEpoch = 0u;
    payload.sourceLightingGeneration = 0u;
    payload.packedDirectionOct32 = 0u;
    payload.generationTimeMixturePdf = 0.0;
    payload.flags = 0u;
    payload.direction = vec3(0.0, 1.0, 0.0);

    uint baseWord;
    if (!TryResolveSimpleDdgiGuidingTransportPayloadRange(
            physicalProbeIndex,
            directionSlot,
            1u,
            directionSlotsPerProbe,
            sidecarPhysicalProbeCapacity,
            baseWord))
    {
        return false;
    }
    uint sidecarIndex = uint(
        SIMPLE_DDGI_GUIDING_DIRECTION_PDF_SIDECAR_BUFFER_INDEX);

    uint abiVersion = ReadStorageWordUniform(sidecarIndex, baseWord + 0u);
    payload.stableProbeId = uvec2(
        ReadStorageWordUniform(sidecarIndex, baseWord + 1u),
        ReadStorageWordUniform(sidecarIndex, baseWord + 2u));
    payload.physicalProbeIndex = ReadStorageWordUniform(
        sidecarIndex, baseWord + 3u);
    payload.virtualProbeId = ReadStorageWordUniform(
        sidecarIndex, baseWord + 4u);
    payload.pageGeneration = ReadStorageWordUniform(
        sidecarIndex, baseWord + 5u);
    payload.distributionGeneration = ReadStorageWordUniform(
        sidecarIndex, baseWord + 6u);
    payload.proposalEpoch = ReadStorageWordUniform(
        sidecarIndex, baseWord + 7u);
    payload.slotIndex = ReadStorageWordUniform(sidecarIndex, baseWord + 8u);
    uint techniqueAndBranch = ReadStorageWordUniform(
        sidecarIndex, baseWord + 9u);
    payload.technique = techniqueAndBranch & 0xffu;
    payload.branch = (techniqueAndBranch >> 8u) & 0xffu;
    payload.sourceEpoch = ReadStorageWordUniform(sidecarIndex, baseWord + 10u);
    payload.sourceLightingGeneration = ReadStorageWordUniform(
        sidecarIndex, baseWord + 11u);
    payload.packedDirectionOct32 = ReadStorageWordUniform(
        sidecarIndex, baseWord + 12u);
    payload.generationTimeMixturePdf = uintBitsToFloat(
        ReadStorageWordUniform(sidecarIndex, baseWord + 13u));
    payload.flags = ReadStorageWordUniform(sidecarIndex, baseWord + 14u);
    uint traceOwnershipTag = ReadStorageWordUniform(
        sidecarIndex,
        baseWord + 15u);

    recordPresent = abiVersion != 0u &&
        any(notEqual(expectedStableProbeId, uvec2(0u))) &&
        all(equal(payload.stableProbeId, expectedStableProbeId)) &&
        payload.physicalProbeIndex == physicalProbeIndex &&
        payload.virtualProbeId == virtualProbeId &&
        payload.pageGeneration == pageGeneration &&
        payload.sourceEpoch == expectedSourceEpoch &&
        payload.sourceLightingGeneration == expectedSourceLightingGeneration;
    if (!recordPresent)
        return false;

    bool techniqueValid = payload.technique ==
            SIMPLE_DDGI_GUIDING_TECHNIQUE_UNIFORM_MAINTENANCE ||
        payload.technique == SIMPLE_DDGI_GUIDING_TECHNIQUE_MIXTURE;
    uint expectedTechnique = SimpleDdgiGuidingIsMaintenanceSlot(
            directionSlot,
            directionSlotsPerProbe)
        ? SIMPLE_DDGI_GUIDING_TECHNIQUE_UNIFORM_MAINTENANCE
        : SIMPLE_DDGI_GUIDING_TECHNIQUE_MIXTURE;
    techniqueValid = techniqueValid &&
        payload.technique == expectedTechnique;
    bool branchValid = payload.branch == SIMPLE_DDGI_GUIDING_BRANCH_UNIFORM ||
        payload.branch == SIMPLE_DDGI_GUIDING_BRANCH_GUIDED;
    bool maintenanceValid = payload.technique !=
            SIMPLE_DDGI_GUIDING_TECHNIQUE_UNIFORM_MAINTENANCE ||
        payload.branch == SIMPLE_DDGI_GUIDING_BRANCH_UNIFORM;
    uint techniqueFlag = payload.technique ==
            SIMPLE_DDGI_GUIDING_TECHNIQUE_UNIFORM_MAINTENANCE
        ? SIMPLE_DDGI_GUIDING_SAMPLE_UNIFORM_MAINTENANCE
        : payload.branch == SIMPLE_DDGI_GUIDING_BRANCH_UNIFORM
            ? SIMPLE_DDGI_GUIDING_SAMPLE_MIXTURE_UNIFORM_BRANCH
            : SIMPLE_DDGI_GUIDING_SAMPLE_MIXTURE_GUIDED_BRANCH;
    bool flagsValid = (payload.flags & ~SIMPLE_DDGI_GUIDING_SAMPLE_KNOWN_FLAGS) == 0u &&
        (payload.flags & SIMPLE_DDGI_GUIDING_SAMPLE_INVALID_DISTRIBUTION) == 0u &&
        (payload.flags & techniqueFlag) != 0u;
    bool identityValid = abiVersion ==
            SIMPLE_DDGI_GUIDING_TRANSPORT_ABI_VERSION &&
        any(notEqual(payload.stableProbeId, uvec2(0u))) &&
        all(equal(payload.stableProbeId, expectedStableProbeId)) &&
        payload.physicalProbeIndex == physicalProbeIndex &&
        payload.virtualProbeId == virtualProbeId &&
        payload.pageGeneration == pageGeneration &&
        payload.sourceEpoch == expectedSourceEpoch &&
        payload.sourceLightingGeneration == expectedSourceLightingGeneration &&
        payload.slotIndex == directionSlot &&
        payload.distributionGeneration != 0u && payload.proposalEpoch != 0u;
    bool pdfValid = !isnan(payload.generationTimeMixturePdf) &&
        !isinf(payload.generationTimeMixturePdf) &&
        payload.generationTimeMixturePdf >=
            0.10 * SIMPLE_DDGI_GUIDING_UNIFORM_SPHERE_PDF;
    uint expectedOwnershipTag = SimpleDdgiGuidingTraceOwnershipTag(
        expectedStableProbeId,
        physicalProbeIndex,
        virtualProbeId,
        pageGeneration,
        expectedSourceEpoch,
        expectedSourceLightingGeneration,
        directionSlot,
        payload.packedDirectionOct32);
    if (!identityValid || !techniqueValid || !branchValid ||
        !maintenanceValid || !flagsValid || !pdfValid ||
        traceOwnershipTag != expectedOwnershipTag)
    {
        return false;
    }

    payload.direction = UnpackSimpleDdgiTransportOctDirection(
        payload.packedDirectionOct32);
    float directionLengthSquared = dot(payload.direction, payload.direction);
    return !any(isnan(payload.direction)) &&
        !any(isinf(payload.direction)) &&
        directionLengthSquared > 0.999 && directionLengthSquared < 1.001;
}

#endif
