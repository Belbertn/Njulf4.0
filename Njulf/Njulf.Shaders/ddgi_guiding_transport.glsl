#ifndef NJULF_DDGI_GUIDING_TRANSPORT_GLSL
#define NJULF_DDGI_GUIDING_TRANSPORT_GLSL

// Compact consumer-side mirror of GPUSimpleDdgiGuidingSamplePayload. The
// standalone train/build/sample shaders include the larger hierarchy ABI;
// ordinary DDGI transport needs only these 16 words and the exact estimator.
const uint SIMPLE_DDGI_GUIDING_TRANSPORT_ABI_VERSION = 0x43330006u;
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
    uint leafIndex;
    uint intraLeafSampleBits;
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

bool SimpleDdgiGuidingTransportProbeHasCompleteBacking(
    uint physicalProbeIndex,
    uint virtualProbeId,
    uint pageGeneration,
    uvec2 expectedStableProbeId,
    uint directionSlotsPerProbe)
{
    if (directionSlotsPerProbe == 0u ||
        directionSlotsPerProbe > SIMPLE_DDGI_MAX_RAYS_PER_PROBE ||
        physicalProbeIndex > 0xffffffffu / directionSlotsPerProbe)
    {
        return false;
    }

    uint firstPayload = physicalProbeIndex * directionSlotsPerProbe;
    if (firstPayload > 0xffffffffu / SIMPLE_DDGI_GUIDING_PAYLOAD_WORDS ||
        directionSlotsPerProbe >
            0xffffffffu / SIMPLE_DDGI_GUIDING_PAYLOAD_WORDS)
    {
        return false;
    }
    uint firstWord = firstPayload * SIMPLE_DDGI_GUIDING_PAYLOAD_WORDS;
    uint probeWords = directionSlotsPerProbe *
        SIMPLE_DDGI_GUIDING_PAYLOAD_WORDS;
    uint sidecarIndex = uint(
        SIMPLE_DDGI_GUIDING_DIRECTION_PDF_SIDECAR_BUFFER_INDEX);
    uint availableWords = uint(BindlessStorageBuffers[
        nonuniformEXT(sidecarIndex)].Words.length());
    if (firstWord > availableWords ||
        probeWords > availableWords - firstWord)
    {
        return false;
    }

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
    return abiVersion != 0u &&
        any(notEqual(expectedStableProbeId, uvec2(0u))) &&
        all(equal(stableProbeId, expectedStableProbeId)) &&
        storedPhysicalProbeIndex == physicalProbeIndex &&
        storedVirtualProbeId == virtualProbeId &&
        storedPageGeneration == pageGeneration;
}

bool TryReadSimpleDdgiGuidingTransportPayload(
    uint physicalProbeIndex,
    uint virtualProbeId,
    uint pageGeneration,
    uvec2 expectedStableProbeId,
    uint directionSlot,
    uint directionSlotsPerProbe,
    out SimpleDdgiGuidingTransportPayload payload)
{
    payload.stableProbeId = uvec2(0u);
    payload.physicalProbeIndex = 0u;
    payload.virtualProbeId = 0u;
    payload.pageGeneration = 0u;
    payload.distributionGeneration = 0u;
    payload.proposalEpoch = 0u;
    payload.slotIndex = 0u;
    payload.technique = SIMPLE_DDGI_GUIDING_TECHNIQUE_UNIFORM_MAINTENANCE;
    payload.branch = SIMPLE_DDGI_GUIDING_BRANCH_UNIFORM;
    payload.leafIndex = 0u;
    payload.intraLeafSampleBits = 0u;
    payload.packedDirectionOct32 = 0u;
    payload.generationTimeMixturePdf = 0.0;
    payload.flags = 0u;
    payload.direction = vec3(0.0, 1.0, 0.0);

    if (!SimpleDdgiGuidingTransportProbeHasCompleteBacking(
            physicalProbeIndex,
            virtualProbeId,
            pageGeneration,
            expectedStableProbeId,
            directionSlotsPerProbe) ||
        directionSlot >= directionSlotsPerProbe ||
        physicalProbeIndex > 0xffffffffu / directionSlotsPerProbe)
    {
        return false;
    }
    uint payloadIndex = physicalProbeIndex * directionSlotsPerProbe +
        directionSlot;
    if (payloadIndex > 0xffffffffu / SIMPLE_DDGI_GUIDING_PAYLOAD_WORDS)
        return false;
    uint baseWord = payloadIndex * SIMPLE_DDGI_GUIDING_PAYLOAD_WORDS;
    uint sidecarIndex = uint(
        SIMPLE_DDGI_GUIDING_DIRECTION_PDF_SIDECAR_BUFFER_INDEX);
    uint availableWords = uint(BindlessStorageBuffers[
        nonuniformEXT(sidecarIndex)].Words.length());
    if (baseWord > availableWords ||
        SIMPLE_DDGI_GUIDING_PAYLOAD_WORDS > availableWords - baseWord)
    {
        return false;
    }

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
    payload.leafIndex = ReadStorageWordUniform(sidecarIndex, baseWord + 10u);
    payload.intraLeafSampleBits = ReadStorageWordUniform(
        sidecarIndex, baseWord + 11u);
    payload.packedDirectionOct32 = ReadStorageWordUniform(
        sidecarIndex, baseWord + 12u);
    payload.generationTimeMixturePdf = uintBitsToFloat(
        ReadStorageWordUniform(sidecarIndex, baseWord + 13u));
    payload.flags = ReadStorageWordUniform(sidecarIndex, baseWord + 14u);
    uint reserved = ReadStorageWordUniform(sidecarIndex, baseWord + 15u);

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
        payload.slotIndex == directionSlot &&
        payload.distributionGeneration != 0u && payload.proposalEpoch != 0u;
    bool pdfValid = !isnan(payload.generationTimeMixturePdf) &&
        !isinf(payload.generationTimeMixturePdf) &&
        payload.generationTimeMixturePdf >=
            0.10 * SIMPLE_DDGI_GUIDING_UNIFORM_SPHERE_PDF;
    if (!identityValid || !techniqueValid || !branchValid ||
        !maintenanceValid || !flagsValid || !pdfValid || reserved != 0u)
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
